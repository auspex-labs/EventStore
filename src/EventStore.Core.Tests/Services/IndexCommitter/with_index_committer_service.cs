using System;
using System.Collections.Generic;
using EventStore.Core.Bus;
using EventStore.Core.Index;
using EventStore.Core.Messages;
using EventStore.Core.Services.Storage;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.Tests.Services.Replication;
using EventStore.Core.Tests.Services.Storage;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.IndexCommitter {
	public abstract class with_index_committer_service {
		protected const int TimeoutSeconds = 5;
		protected string EventStreamId = "test_stream";
		protected int CommitCount = 2;
		protected ITableIndex TableIndex;

		protected ICheckpoint ReplicationCheckpoint;
		protected ICheckpoint WriterCheckpoint;
		protected InMemoryBus Publisher = new InMemoryBus("publisher");
		protected List<StorageMessage.CommitIndexed> CommitReplicatedMgs = new List<StorageMessage.CommitIndexed>();
		protected List<ReplicationTrackingMessage.IndexedTo> IndexWrittenMgs = new List<ReplicationTrackingMessage.IndexedTo>();

		protected IndexCommitterService Service;
		protected FakeIndexCommitter IndexCommitter;
		protected ITFChunkScavengerLogManager TfChunkScavengerLogManager;
		
		[OneTimeSetUp]
		public virtual void TestFixtureSetUp() {
			IndexCommitter = new FakeIndexCommitter();
			ReplicationCheckpoint = new InMemoryCheckpoint();
			WriterCheckpoint = new InMemoryCheckpoint(0);
			Publisher.Subscribe(new AdHocHandler<StorageMessage.CommitIndexed>(m => CommitReplicatedMgs.Add(m)));
			Publisher.Subscribe(new AdHocHandler<ReplicationTrackingMessage.IndexedTo>(m => IndexWrittenMgs.Add(m)));
			TableIndex = new FakeTableIndex();
			TfChunkScavengerLogManager = new FakeTfChunkLogManager();
			Service = new IndexCommitterService(IndexCommitter, Publisher, WriterCheckpoint, ReplicationCheckpoint, CommitCount, TableIndex, new QueueStatsManager());
			Service.Init(0);
			Publisher.Subscribe<ReplicationTrackingMessage.ReplicatedTo>(Service);
			Given();

			When();
		}

		[OneTimeTearDown]
		public virtual void TestFixtureTearDown() {
			Service.Stop();
		}
		public abstract void Given();
		public abstract void When();

		protected void AddPendingPrepare(long transactionPosition, long postPosition = -1) {
			postPosition = postPosition == -1 ? transactionPosition : postPosition;
			var prepare = CreatePrepare(transactionPosition, transactionPosition);
			Service.AddPendingPrepare(new[] { prepare }, postPosition);
		}

		protected void AddPendingPrepares(long transactionPosition, long[] logPositions) {
			var prepares = new List<PrepareLogRecord>();
			foreach (var pos in logPositions) {
				prepares.Add(CreatePrepare(transactionPosition, pos));
			}

			Service.AddPendingPrepare(prepares.ToArray(), logPositions[^1]);
		}

		private PrepareLogRecord CreatePrepare(long transactionPosition, long logPosition) {
			return LogRecord.Prepare(logPosition, Guid.NewGuid(), Guid.NewGuid(), transactionPosition, 0,
				EventStreamId, -1, PrepareFlags.None, "testEvent",
				new byte[10], new byte[0]);
		}


		protected void AddPendingCommit(long transactionPosition, long logPosition, long postPosition = -1) {
			postPosition = postPosition == -1 ? logPosition : postPosition;
			var commit = LogRecord.Commit(logPosition, Guid.NewGuid(), transactionPosition, 0);
			Service.AddPendingCommit(commit, postPosition);
		}
	}

	public class FakeIndexCommitter : IIndexCommitter {
		public List<PrepareLogRecord> CommittedPrepares = new List<PrepareLogRecord>();
		public List<CommitLogRecord> CommittedCommits = new List<CommitLogRecord>();

		public long LastIndexedPosition { get; set; }

		public void Init(long buildToPosition) {
		}

		public void Dispose() {
		}

		public long Commit(CommitLogRecord commit, bool isTfEof, bool cacheLastEventNumber) {
			CommittedCommits.Add(commit);
			return 0;
		}

		public long Commit(IList<PrepareLogRecord> committedPrepares, bool isTfEof, bool cacheLastEventNumber) {
			CommittedPrepares.AddRange(committedPrepares);
			return 0;
		}

		public long GetCommitLastEventNumber(CommitLogRecord commit) {
			return 0;
		}
	}
}