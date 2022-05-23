﻿using System;
using System.Collections.Generic;
using EventStore.Core.Data;
using EventStore.Core.Services.Storage.ReaderIndex;

namespace EventStore.Core.TransactionLog.Scavenging {
	public class IndexReaderForCalculator : IIndexReaderForCalculator<string> {
		private readonly IReadIndex _readIndex;
		private readonly Func<ulong, string> _getStreamId = x => throw new NotImplementedException();

		public IndexReaderForCalculator(IReadIndex readIndex) {
			_readIndex = readIndex;
		}

		//qq add unit tests where required from here down
		public long GetLastEventNumber(StreamHandle<string> handle, ScavengePoint scavengePoint) {
			switch (handle.Kind) {
				case StreamHandle.Kind.Hash:
					// tries as far as possible to use the index without consulting the log to fetch the last event number
					return _readIndex.GetStreamLastEventNumber_NoCollisions(handle.StreamHash, _getStreamId, scavengePoint.Position);
				case StreamHandle.Kind.Id:
					// uses the index and the log to fetch the last event number
					return _readIndex.GetStreamLastEventNumber_KnownCollisions(handle.StreamId, scavengePoint.Position);
				default:
					throw new ArgumentOutOfRangeException(nameof(handle), handle, null);
			}
		}

		//qq add unit tests where required from here down
		public EventInfo[] ReadEventInfoForward(
			StreamHandle<string> handle,
			long fromEventNumber,
			int maxCount,
			ScavengePoint scavengePoint) { //qq account for scavengepoint

			switch (handle.Kind) {
				case StreamHandle.Kind.Hash:
					//qqqqqqq the Id case deduplicates, we probably want this case to as well, according to skipindexscanonread
					// uses the index only
					return _readIndex.ReadEventInfoForward(handle.StreamHash, fromEventNumber, maxCount,
						scavengePoint.Position).EventInfos;
				case StreamHandle.Kind.Id:
					// uses log to check for hash collisions
					//qqqq if the stream is deleted i expect this wont let us read it at all but we do
					//need to
					var result = _readIndex.ReadStreamEventsForward(
						handle.StreamId,
						fromEventNumber,
						maxCount);

					//qq do we need to look at the other things like .Result, .IsEndOfStream etc
					var eventInfos = new List<EventInfo>();
					foreach (var record in result.Records) {
						if (record.LogPosition < scavengePoint.Position) {
							eventInfos.Add(new EventInfo(record.LogPosition, record.EventNumber));
						}
					}
					return eventInfos.ToArray();
				default:
					throw new ArgumentOutOfRangeException(nameof(handle), handle, null);
			}
		}
	}
}