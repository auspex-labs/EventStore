﻿using System;
using System.Threading;

namespace EventStore.Core.TransactionLog.Scavenging {
	public class Calculator<TStreamId> : ICalculator<TStreamId> {
		private readonly IIndexReaderForCalculator<TStreamId> _index;
		private readonly int _chunkSize;
		private readonly int _cancellationCheckPeriod;
		private readonly int _checkpointPeriod;

		public Calculator(
			IIndexReaderForCalculator<TStreamId> index,
			int chunkSize,
			int cancellationCheckPeriod,
			int checkpointPeriod) {

			_index = index;
			_chunkSize = chunkSize;
			_cancellationCheckPeriod = cancellationCheckPeriod;
			_checkpointPeriod = checkpointPeriod;
		}

		public void Calculate(
			ScavengePoint scavengePoint,
			IScavengeStateForCalculator<TStreamId> state,
			CancellationToken cancellationToken) {

			var checkpoint = new ScavengeCheckpoint.Calculating<TStreamId>(
				scavengePoint: scavengePoint,
				doneStreamHandle: default);
			state.SetCheckpoint(checkpoint);
			Calculate(checkpoint, state, cancellationToken);
		}

		public void Calculate(
			ScavengeCheckpoint.Calculating<TStreamId> checkpoint,
			IScavengeStateForCalculator<TStreamId> state,
			CancellationToken cancellationToken) {

			var weights = new WeightCalculator<TStreamId>(state);
			var scavengePoint = checkpoint.ScavengePoint;
			var streamCalc = new StreamCalculator<TStreamId>(_index, scavengePoint);
			var eventCalc = new EventCalculator<TStreamId>(_chunkSize, state, scavengePoint, streamCalc);

			var checkpointCounter = 0;
			var cancellationCheckCounter = 0;

			// iterate through the original (i.e. non-meta) streams that need scavenging (i.e.
			// those that have metadata or tombstones)
			// - for each one use the accumulated data to set/update the discard points of the stream.
			// - along the way add weight to the affected chunks.
			var originalStreamsToScavenge = state.OriginalStreamsToScavenge(
				checkpoint: checkpoint?.DoneStreamHandle ?? default);

			// for determinism it is important that IncreaseChunkWeight is called in a transaction with
			// its calculation and checkpoint, otherwise the weight could be increased again on recovery
			var transaction = state.BeginTransaction();
			try {
				foreach (var (originalStreamHandle, originalStreamData) in originalStreamsToScavenge) {
					//qqqqqqqqqqqqqqqqqqq
					// it would be neat if this interface gave us some hint about the location of
					// the DP so that we could set it in a moment cheaply without having to search.
					// although, if its a wal that'll be cheap anyway.
					// if the scavengemap supports RMW that might have a bearing too, but for now maybe
					// this is just overcomplicating things.

					//qq there is probably scope for a few optimisations here eg, we could store on the
					// originalstreamdata what the lasteventnumber was at the point of calculating the 
					// discard points. then we could spot here that if the last event number hasn't moved
					// and the the metadata hasn't changed, then the DP wont have moved for maxcount.
					// consider the equivalent for the other discard criteria, and see whether the time/space
					// tradeoff is worth it.
					//
					//qq tidy: we might also remove from OriginalStreamsToScavenge when the TB or tombstone 
					// is completely spent, which might have a bearing on the above.
					streamCalc.SetStream(originalStreamHandle, originalStreamData);

					CalculateDiscardPointsForOriginalStream(
						eventCalc,
						weights,
						originalStreamHandle,
						scavengePoint,
						out var adjustedDiscardPoint,
						out var adjustedMaybeDiscardPoint);

					// don't allow the discard point to move backwards
					if (adjustedDiscardPoint < originalStreamData.DiscardPoint) {
						adjustedDiscardPoint = originalStreamData.DiscardPoint;
					}

					// don't allow the maybe discard point to move backwards
					if (adjustedMaybeDiscardPoint < originalStreamData.MaybeDiscardPoint) {
						adjustedMaybeDiscardPoint = originalStreamData.MaybeDiscardPoint;
					}

					if (adjustedDiscardPoint == originalStreamData.DiscardPoint &&
						adjustedMaybeDiscardPoint == originalStreamData.MaybeDiscardPoint) {
						// nothing to update for this stream
					} else {
						state.SetOriginalStreamDiscardPoints(
							streamHandle: originalStreamHandle,
							discardPoint: adjustedDiscardPoint,
							maybeDiscardPoint: adjustedMaybeDiscardPoint);
					}

					// Check cancellation occasionally
					if (++cancellationCheckCounter == _cancellationCheckPeriod) {
						cancellationCheckCounter = 0;
						cancellationToken.ThrowIfCancellationRequested();
					}

					// Checkpoint occasionally
					if (++checkpointCounter == _checkpointPeriod) {
						checkpointCounter = 0;
						weights.Flush();
						transaction.Commit(new ScavengeCheckpoint.Calculating<TStreamId>(
							scavengePoint,
							originalStreamHandle));
						transaction = state.BeginTransaction();
					}
				}

				//qqqqqq consider/test this
				// we have an open transaction here so we have to commit something
				// if we processed some streams, the last one is in the calculator
				// if we didn't process any streams, the calculator contains the default
				// none handle, which is probably appropriate to commit in that case
				weights.Flush();
				transaction.Commit(new ScavengeCheckpoint.Calculating<TStreamId>(
					scavengePoint,
					streamCalc.OriginalStreamHandle));
			} catch {
				transaction.Rollback();
				throw;
			}
		}

		// This does two things.
		// 1. Calculates and returns the discard points for this stream
		// 2. Adds weight to the affected chunks so that they get scavenged.
		//
		// The calculator determines that we can definitely discard everything up to the discardPoint
		// and we may be able to discard things between the discardPoint and the maybeDiscardPoint.
		//
		// We want to calculate the discard points from scratch, without considering what values they
		// came out as last time.
		private void CalculateDiscardPointsForOriginalStream(
			EventCalculator<TStreamId> eventCalc,
			WeightCalculator<TStreamId> weights,
			StreamHandle<TStreamId> originalStreamHandle,
			ScavengePoint scavengePoint,
			out DiscardPoint discardPoint,
			out DiscardPoint maybeDiscardPoint) {

			var fromEventNumber = 0L;

			discardPoint = DiscardPoint.KeepAll;
			maybeDiscardPoint = DiscardPoint.KeepAll;

			const int maxCount = 100; //qq what would be sensible? probably pretty large

			var first = true;

			while (true) {
				// read in slices because the stream might be huge.
				// note: when the handle is a hash the ReadEventInfoForward call is index-only
				// note: the event infos are not necessarily contiguous
				//qq limit the read to the scavengepoint too?
				var slice = _index.ReadEventInfoForward(
					originalStreamHandle,
					fromEventNumber,
					maxCount,
					scavengePoint);

				foreach (var eventInfo in slice) {
					eventCalc.SetEvent(eventInfo);

					if (first) {
						// this is the first event that is known to the index. advance the discard points
						// to discard everything before here since they're already discarded. (we need
						// this because the chunks haven't necessarily been executed yet so we want to
						// make sure those records are removed when the chunks are scavenged. note that
						// chunk weight has already been added for them, so no need to do that again.
						discardPoint = DiscardPoint.DiscardBefore(eventInfo.EventNumber);
						maybeDiscardPoint = discardPoint;
						first = false;
					}

					switch (eventCalc.DecideEvent()) {
						case DiscardDecision.Discard:
							weights.OnDiscard(eventCalc.LogicalChunkNumber);
							discardPoint = DiscardPoint.DiscardIncluding(eventInfo.EventNumber);
							break;

						case DiscardDecision.MaybeDiscard:
							// add weight to the chunk so that this will be inspected more closely
							// it is possible that we already added weight on a previous scavenge and are
							// doing so again, but we must because the weight may have been reset
							// by the previous scavenge
							weights.OnMaybeDiscard(eventCalc.LogicalChunkNumber);
							maybeDiscardPoint = DiscardPoint.DiscardIncluding(eventInfo.EventNumber);
							break;

						case DiscardDecision.Keep:
							// found the first one to keep. we are done discarding. to help keep things
							// simple, move the maybe up to the discardpoint if it is behind.
							maybeDiscardPoint = maybeDiscardPoint.Or(discardPoint);
							return;

						default:
							throw new Exception("sdfhg"); //qq detail
					}
				}

				// we haven't found an event to definitely keep
				//qq would it be better to have IsEndOfStream returned from the index?
				if (slice.Length < maxCount) {
					// we have finished reading the stream from the index,
					// but not found any events to keep.
					// we therefore didn't find any at all, or found some and discarded them all
					// (the latter should not be possible)
					if (first) {
						// we didn't find any at all
						// - the stream might actually be empty
						// - the stream might have events after the scavenge point and olscavenge
						//   has removed the ones before
						// we didn't find anything to discard, so keep everything.
						// in these situatiosn what discard point should we return, or do we need to abort
						discardPoint = DiscardPoint.KeepAll;
						maybeDiscardPoint = DiscardPoint.KeepAll;
						return;
					} else {
						// we found some and discarded them all, oops.
						//qq maybe we could have the state look up the stream name
						throw new Exception(
							$"Discarded all events for stream {originalStreamHandle}. " +
							$"This should be impossible.");
					}
				} else {
					// we aren't done reading slices, read the next slice.
				}

				fromEventNumber += slice.Length;
			}
		}
	}
}
