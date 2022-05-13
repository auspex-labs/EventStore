﻿using EventStore.Core.TransactionLog.Scavenging;

namespace EventStore.Core.XUnit.Tests.Scavenge {
	public class TracingTransactionManager : ITransactionManager {
		private readonly ITransactionManager _wrapped;
		private readonly Tracer _tracer;

		public TracingTransactionManager(ITransactionManager wrapped, Tracer tracer) {
			_wrapped = wrapped;
			_tracer = tracer;
		}

		public void Begin() {
			_wrapped.Begin();
		}

		public void Commit(ScavengeCheckpoint checkpoint) {
			_tracer.Trace($"Checkpoint: {checkpoint}");
			_wrapped.Commit(checkpoint);
		}

		public void Rollback() {
			_wrapped.Rollback();
		}
	}
}