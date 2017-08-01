﻿//  ----------------------------------------------------------------------------------
//  Copyright Microsoft Corporation
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//  http://www.apache.org/licenses/LICENSE-2.0
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//  ----------------------------------------------------------------------------------

namespace DurableTask.ServiceFabric
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.ServiceFabric.Data;
    using Microsoft.ServiceFabric.Data.Collections;

    class SessionsProvider : MessageProviderBase<string, PersistentSession>
    {
        ConcurrentQueue<string> inMemorySessionsQueue = new ConcurrentQueue<string>();
        ConcurrentDictionary<string, LockState> lockedSessions = new ConcurrentDictionary<string, LockState>();

        ConcurrentDictionary<OrchestrationInstance, SessionMessagesProvider<Guid, TaskMessageItem>> sessionMessageProviders = new ConcurrentDictionary<OrchestrationInstance, SessionMessagesProvider<Guid, TaskMessageItem>>(OrchestrationInstanceComparer.Default);

        public SessionsProvider(IReliableStateManager stateManager, CancellationToken token) : base(stateManager, Constants.OrchestrationDictionaryName, token)
        {
        }

        public async Task<PersistentSession> AcceptSessionAsync(TimeSpan receiveTimeout)
        {
            if (!IsStopped())
            {
                string returnInstanceId;
                bool newItemsBeforeTimeout = true;
                while (newItemsBeforeTimeout)
                {
                    if (this.inMemorySessionsQueue.TryDequeue(out returnInstanceId))
                    {
                        try
                        {
                            return await RetryHelper.ExecuteWithRetryOnTransient(async () =>
                            {
                                using (var txn = this.StateManager.CreateTransaction())
                                {
                                    var existingValue = await this.Store.TryGetValueAsync(txn, returnInstanceId);

                                    if (existingValue.HasValue)
                                    {
                                        if (!this.lockedSessions.TryUpdate(returnInstanceId, newValue: LockState.Locked, comparisonValue: LockState.InFetchQueue))
                                        {
                                            var errorMessage = $"Internal Server Error : Unexpected to dequeue the session {returnInstanceId} which was already locked before";
                                            ProviderEventSource.Log.UnexpectedCodeCondition(errorMessage);
                                            throw new Exception(errorMessage);
                                        }

                                        ProviderEventSource.Log.TraceMessage(returnInstanceId, "Session Locked Accepted");
                                        return existingValue.Value;
                                    }
                                    else
                                    {
                                        var errorMessage = $"Internal Server Error: Did not find the session object in reliable dictionary while having the session {returnInstanceId} in memory";
                                        ProviderEventSource.Log.UnexpectedCodeCondition(errorMessage);
                                        throw new Exception(errorMessage);
                                    }
                                }
                            }, uniqueActionIdentifier: $"{nameof(SessionsProvider)}.{nameof(AcceptSessionAsync)}, SessionId : {returnInstanceId}");
                        }
                        catch (Exception)
                        {
                            this.inMemorySessionsQueue.Enqueue(returnInstanceId);
                            throw;
                        }
                    }

                    newItemsBeforeTimeout = await WaitForItemsAsync(receiveTimeout);
                }
            }
            return null;
        }

        protected override void AddItemInMemory(string key, PersistentSession value)
        {
            this.TryEnqueueSession(key);
        }

        public async Task<List<Message<Guid, TaskMessageItem>>> ReceiveSessionMessagesAsync(PersistentSession session)
        {
            var sessionMessageProvider = await GetOrAddSessionMessagesInstance(session.SessionId);
            var messages = await sessionMessageProvider.ReceiveBatchAsync();
            ProviderEventSource.Log.TraceMessage(session.SessionId.InstanceId, $"Number of received messages {messages.Count}");
            return messages;
        }

        public async Task CompleteMessages(ITransaction transaction, OrchestrationInstance instance, List<Guid> lockTokens)
        {
            SessionMessagesProvider<Guid, TaskMessageItem> sessionMessageProvider;
            if (this.sessionMessageProviders.TryGetValue(instance, out sessionMessageProvider))
            {
                ProviderEventSource.Log.TraceMessage(instance.InstanceId, $"Number of completed messages {lockTokens.Count}");
                await sessionMessageProvider.CompleteBatchAsync(transaction, lockTokens);
            }
            else
            {
                ProviderEventSource.Log.UnexpectedCodeCondition($"{nameof(SessionsProvider)}.{nameof(CompleteMessages)} : Did not find session messages provider instance for session : {instance}.");
            }
        }

        public async Task UpdateSessionState(ITransaction transaction, OrchestrationInstance instance, OrchestrationRuntimeState newSessionState)
        {
            var sessionStateEvents = newSessionState?.Events.ToImmutableList();
            var result = PersistentSession.Create(instance, sessionStateEvents);
#if DEBUG
            ProviderEventSource.Log.LogSizeMeasure($"Value in SessionsProvider for SessionId = {instance.InstanceId}", DebugSerializationUtil.GetDataContractSerializationSize(result));
#endif
            await this.Store.SetAsync(transaction, instance.InstanceId, result);
        }

        public async Task AppendMessageAsync(TaskMessageItem newMessage)
        {
            await RetryHelper.ExecuteWithRetryOnTransient(async () =>
            {
                using (var txn = this.StateManager.CreateTransaction())
                {
                    await this.AppendMessageAsync(txn, newMessage);
                    await txn.CommitAsync();
                }
            }, uniqueActionIdentifier: $"Orchestration = '{newMessage.TaskMessage.OrchestrationInstance}', Action = '{nameof(SessionsProvider)}.{nameof(AppendMessageAsync)}'");

            this.TryEnqueueSession(newMessage.TaskMessage.OrchestrationInstance);
        }

        public async Task AppendMessageAsync(ITransaction transaction, TaskMessageItem newMessage)
        {
            ThrowIfStopped();
            await EnsureStoreInitialized();

            var sessionMessageProvider = await GetOrAddSessionMessagesInstance(newMessage.TaskMessage.OrchestrationInstance);
            await sessionMessageProvider.SendBeginAsync(transaction, new Message<Guid, TaskMessageItem>(Guid.NewGuid(), newMessage));
            await this.Store.TryAddAsync(transaction, newMessage.TaskMessage.OrchestrationInstance.InstanceId, PersistentSession.Create(newMessage.TaskMessage.OrchestrationInstance));
        }

        public async Task<bool> TryAppendMessageAsync(ITransaction transaction, TaskMessageItem newMessage)
        {
            ThrowIfStopped();

            if (await this.Store.ContainsKeyAsync(transaction, newMessage.TaskMessage.OrchestrationInstance.InstanceId))
            {
                var sessionMessageProvider = await GetOrAddSessionMessagesInstance(newMessage.TaskMessage.OrchestrationInstance);
                await sessionMessageProvider.SendBeginAsync(transaction, new Message<Guid, TaskMessageItem>(Guid.NewGuid(), newMessage));
                return true;
            }

            return false;
        }

        public async Task<IList<OrchestrationInstance>> TryAppendMessageBatchAsync(ITransaction transaction, IEnumerable<TaskMessageItem> newMessages)
        {
            ThrowIfStopped();
            List<OrchestrationInstance> modifiedSessions = new List<OrchestrationInstance>();

            var groups = newMessages.GroupBy(m => m.TaskMessage.OrchestrationInstance, OrchestrationInstanceComparer.Default);

            foreach (var group in groups)
            {
                if (await this.Store.ContainsKeyAsync(transaction, group.Key.InstanceId))
                {
                    var sessionMessageProvider = await GetOrAddSessionMessagesInstance(group.Key);
                    await sessionMessageProvider.SendBatchBeginAsync(transaction, group.Select(tm => new Message<Guid, TaskMessageItem>(Guid.NewGuid(), tm)));
                    modifiedSessions.Add(group.Key);
                }
            }

            return modifiedSessions;
        }

        public async Task AppendMessageBatchAsync(ITransaction transaction, IEnumerable<TaskMessageItem> newMessages)
        {
            ThrowIfStopped();
            var groups = newMessages.GroupBy(m => m.TaskMessage.OrchestrationInstance, OrchestrationInstanceComparer.Default);

            foreach (var group in groups)
            {
                var groupMessages = group.AsEnumerable();

                await this.Store.TryAddAsync(transaction, group.Key.InstanceId, PersistentSession.Create(group.Key));
                var sessionMessageProvider = await GetOrAddSessionMessagesInstance(group.Key);
                await sessionMessageProvider.SendBatchBeginAsync(transaction, group.Select(tm => new Message<Guid, TaskMessageItem>(Guid.NewGuid(), tm)));
            }
        }

        public async Task<IEnumerable<PersistentSession>> GetSessions()
        {
            await EnsureStoreInitialized();

            var result = new List<PersistentSession>();
            await this.EnumerateItems(kvp => result.Add(kvp.Value));
            return result;
        }

        public void TryUnlockSession(OrchestrationInstance instance, bool abandon = false, bool isComplete = false)
        {
            ProviderEventSource.Log.TraceMessage(instance.InstanceId, $"Session Unlock Begin, Abandon = {abandon}");
            LockState lockState;
            if (!this.lockedSessions.TryRemove(instance.InstanceId, out lockState) || lockState == LockState.InFetchQueue)
            {
                var errorMessage = $"{nameof(SessionsProvider)}.{nameof(TryUnlockSession)} : Trying to unlock the session {instance.InstanceId} which was not locked.";
                ProviderEventSource.Log.UnexpectedCodeCondition(errorMessage);
                throw new Exception(errorMessage);
            }

            if (!isComplete && (abandon || lockState == LockState.NewMessagesWhileLocked))
            {
                this.TryEnqueueSession(instance);
            }
            ProviderEventSource.Log.TraceMessage(instance.InstanceId, $"Session Unlock End, Abandon = {abandon}, removed lock state = {lockState}");
        }

        public async Task<bool> TryAddSession(ITransaction transaction, TaskMessageItem newMessage)
        {
            ThrowIfStopped();
            await EnsureStoreInitialized();

            bool added = await this.Store.TryAddAsync(transaction, newMessage.TaskMessage.OrchestrationInstance.InstanceId, PersistentSession.Create(newMessage.TaskMessage.OrchestrationInstance));

            if (added)
            {
                var sessionMessageProvider = await GetOrAddSessionMessagesInstance(newMessage.TaskMessage.OrchestrationInstance);
                await sessionMessageProvider.SendBeginAsync(transaction, new Message<Guid, TaskMessageItem>(Guid.NewGuid(), newMessage));
            }

            return added;
        }

        public async Task<PersistentSession> GetSession(string instanceId)
        {
            await EnsureStoreInitialized();

            return await RetryHelper.ExecuteWithRetryOnTransient(async () =>
            {
                using (var txn = this.StateManager.CreateTransaction())
                {
                    var value = await this.Store.TryGetValueAsync(txn, instanceId);
                    if (value.HasValue)
                    {
                        return value.Value;
                    }
                }
                return null;
            }, uniqueActionIdentifier: $"Orchestration InstanceId = {instanceId}, Action = {nameof(SessionsProvider)}.{nameof(GetSession)}");
        }

        public void TryEnqueueSession(OrchestrationInstance instance)
        {
            this.TryEnqueueSession(instance.InstanceId);
        }

        void TryEnqueueSession(string instanceId)
        {
            bool enqueue = true;
            this.lockedSessions.AddOrUpdate(instanceId, LockState.InFetchQueue, (ses, old) =>
            {
                enqueue = false;
                return old == LockState.Locked ? LockState.NewMessagesWhileLocked : old;
            });

            if (enqueue)
            {
                ProviderEventSource.Log.TraceMessage(instanceId, "Session Getting Enqueued");
                this.inMemorySessionsQueue.Enqueue(instanceId);
                SetWaiterForNewItems();
            }
        }

        public async Task DropSession(ITransaction txn, OrchestrationInstance instance)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            await this.Store.TryRemoveAsync(txn, instance.InstanceId);

            SessionMessagesProvider<Guid, TaskMessageItem> sessionMessagesProvider;
            this.sessionMessageProviders.TryRemove(instance, out sessionMessagesProvider);

            var noWait = RetryHelper.ExecuteWithRetryOnTransient(() => this.StateManager.RemoveAsync(GetSessionMessagesDictionaryName(instance)),
                uniqueActionIdentifier: $"Orchestration = '{instance}', Action = 'DropSessionMessagesDictionaryBackgroundTask'");
        }

        async Task<SessionMessagesProvider<Guid, TaskMessageItem>> GetOrAddSessionMessagesInstance(OrchestrationInstance instance)
        {
            var newInstance = new SessionMessagesProvider<Guid, TaskMessageItem>(this.StateManager, GetSessionMessagesDictionaryName(instance), this.CancellationToken);
            var sessionMessageProvider = this.sessionMessageProviders.GetOrAdd(instance, newInstance);
            await sessionMessageProvider.EnsureStoreInitialized();
            return sessionMessageProvider;
        }

        string GetSessionMessagesDictionaryName(OrchestrationInstance instance)
        {
            var sessionKey = $"{instance.InstanceId}_{instance.ExecutionId}";
            return Constants.SessionMessagesDictionaryPrefix + sessionKey;
        }

        enum LockState
        {
            InFetchQueue = 0,

            Locked,

            NewMessagesWhileLocked
        }
    }
}
