﻿using System;
using System.Threading;
using EventStore.Common.Log;
using EventStore.Core.Index;

namespace EventStore.Core.TransactionLog.Scavenging {
	public class IndexExecutor {
		protected static readonly ILogger Log = LogManager.GetLoggerFor<IndexExecutor>();
	}

	public class IndexExecutor<TStreamId> : IndexExecutor, IIndexExecutor<TStreamId> {
		private readonly IIndexScavenger _indexScavenger;
		private readonly IChunkReaderForIndexExecutor<TStreamId> _streamLookup;
		private readonly bool _unsafeIgnoreHardDeletes;

		public IndexExecutor(
			IIndexScavenger indexScavenger,
			IChunkReaderForIndexExecutor<TStreamId> streamLookup,
			bool unsafeIgnoreHardDeletes) {

			_indexScavenger = indexScavenger;
			_streamLookup = streamLookup;
			_unsafeIgnoreHardDeletes = unsafeIgnoreHardDeletes;
		}

		public void Execute(
			ScavengePoint scavengePoint,
			IScavengeStateForIndexExecutor<TStreamId> state,
			IIndexScavengerLog scavengerLogger,
			CancellationToken cancellationToken) {

			Log.Trace("Starting new scavenge index execution phase for {scavengePoint}",
				scavengePoint.GetName());

			var checkpoint = new ScavengeCheckpoint.ExecutingIndex(scavengePoint);
			state.SetCheckpoint(checkpoint);
			Execute(checkpoint, state, scavengerLogger, cancellationToken);
		}

		public void Execute(
			ScavengeCheckpoint.ExecutingIndex checkpoint,
			IScavengeStateForIndexExecutor<TStreamId> state,
			IIndexScavengerLog scavengerLogger,
			CancellationToken cancellationToken) {

			Log.Trace("Executing indexes from checkpoint: {checkpoint}", checkpoint);

			_indexScavenger.ScavengeIndex(
				scavengePoint: checkpoint.ScavengePoint.Position,
				shouldKeep: GenShouldKeep(state),
				log: scavengerLogger,
				cancellationToken: cancellationToken);
		}

		private Func<IndexEntry, bool> GenShouldKeep(IScavengeStateForIndexExecutor<TStreamId> state) {
			//qq cache some info between invocations of ShouldKeep since it will typically be invoked
			// repeatedly with the same stream hash.
			//
			// invariants, guaranteed at the beginning and end of each Invokation of ShouldKeep:
			//  (a) currentHash is not null =>
			//         currentHashIsCollision iff currentHash is a collision
			//
			//  (b) currentHash is not null && !currentHashIsCollision =>
			//         currentDiscardPoint is the discardpoint of the unique stream
			//         that hashes to currentHash.
			//qq hum there should be another invariant here about when currentHash is a collision
			var currentHash = (ulong?)null;
			var currentHashIsCollision = false;
			var currentDiscardPoint = DiscardPoint.KeepAll;
			var currentIsTombstoned = false;
			//qq this isn't always defined, consider in relationship to the invariants.
			var currentIsDefinitelyMetastream = false;

			bool ShouldKeep(IndexEntry indexEntry) {
				//qq throttle?
				//qqqq need to respect the scavenge point
				if (currentHash != indexEntry.Stream || currentHashIsCollision) {
					// currentHash != indexEntry.Stream || currentHashIsCollision
					// we are on to a new stream, or the hash collides so we _might_ be on
					// to a new stream.

					// bring currentHash up to date.
					currentHash = indexEntry.Stream;
					// re-establish (a)
					currentHashIsCollision = state.IsCollision(indexEntry.Stream);

					StreamHandle<TStreamId> handle = default;

					if (currentHashIsCollision) {
						// (b) is re-established because currentHashIsCollision is true collision, so
						// the hash itself does not identify the stream. need to look it up.
						if (!_streamLookup.TryGetStreamId(indexEntry.Position, out var streamId)) {
							// there is no record at this position to get the stream from.
							// we should definitely discard the entry (just like old index scavenge does)
							// we can't even tell which stream it is for.
							return false;
						} else {
							// we got a streamId, which means we must have found a record at this
							// position, but that doesn't necessarily mean we want to keep the IndexEntry
							// the log record might still exist only because its chunk hasn't reached
							// the threshold.
							handle = StreamHandle.ForStreamId(streamId);
						}
					} else {
						// not a collision, we can get the discard point by hash.
						handle = StreamHandle.ForHash<TStreamId>(currentHash.Value);
					}

					//qq memoize to speed up other ptables?
					// ^ (consider this generally for the scavenge state)
					// re-establish (b)
					if (state.TryGetIndexExecutionInfo(handle, out var info)) {
						currentIsTombstoned = info.IsTombstoned;
						currentDiscardPoint = info.DiscardPoint;
						currentIsDefinitelyMetastream = info.IsMetastream;
					} else {
						// this stream has no discard point. keep everything.
						currentIsTombstoned = false;
						currentDiscardPoint = DiscardPoint.KeepAll;
						currentIsDefinitelyMetastream = false;
						return true;
					}
				} else {
					// same hash as the previous invocation, and it is not a collision, so it must be for
					// the same stream, so the currentDiscardPoint applies.
					// invariants already established.
					;
				}

				if (currentIsTombstoned) {
					if (_unsafeIgnoreHardDeletes) {
						// remove _everything_ for metadata and original streams
						return false;
					}

					if (currentIsDefinitelyMetastream) {
						// when the original stream is tombstoned we can discard the _whole_ metastream
						return false;
					}

					// otherwise obey the discard points below.
				}

				var shouldDiscard = currentDiscardPoint.ShouldDiscard(indexEntry.Version);
				return !shouldDiscard;
			}

			return ShouldKeep;
		}
	}
}
