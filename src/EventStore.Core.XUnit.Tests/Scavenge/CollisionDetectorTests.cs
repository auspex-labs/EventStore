﻿using System;
using System.Collections.Generic;
using System.Linq;
using EventStore.Core.Tests.Index.Hashers;
using EventStore.Core.TransactionLog.Scavenging;
using EventStore.Core.TransactionLog.Scavenging.Sqlite;
using EventStore.Core.XUnit.Tests.Scavenge.Sqlite;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Scavenge {
	public class CollisionDetectorTests : SqliteDbPerTest<CollisionDetectorTests> {
		public static IEnumerable<object[]> TheCases() {
			var none = Array.Empty<string>();
			
			// the first letter of the stream name determines the the hash
			// each row represents a record but here the only thing we need to know about it is
			// its streamName and the collisions it generates when we add it.
			yield return Case(
				"seen stream before. was a collision first time we saw it",
				("a-stream1", none),
				("a-streamOfInterest", new[] { "a-stream1", "a-streamOfInterest" }),
				("b-stream2", none),
				("a-streamOfInterest", none));

			yield return Case(
				"seen stream before. was not a collision but has since been collided with",
				("a-streamOfInterest", none),
				("a-stream1", new[] { "a-stream1", "a-streamOfInterest" }),
				("b-stream2", none),
				("a-streamOfInterest", none));

			yield return Case(
				"seen stream before. was not a collision and still isnt",
				("a-streamOfInterest", none),
				("b-stream2", none),
				("a-streamOfInterest", none));

			yield return Case(
				"first time seeing stream. no collision",
				("a-stream1", none),
				("b-stream2", none),
				("c-streamOfInterest", none));

			yield return Case(
				"first time seeing stream. collides with previous stream",
				("a-stream1", none),
				("b-stream2", none),
				("a-streamOfInterest", new[] { "a-stream1", "a-streamOfInterest" }));

			yield return Case(
				"three way collision",
				("a-stream1", none),
				("a-stream2", new[] { "a-stream1", "a-stream2" }),
				("a-stream3", new[] { "a-stream3" }));

			yield return Case(
				"in combination",
				("a-stream1", none), // 2b
				("a-stream2", new[] { "a-stream1", "a-stream2" }), // 2a
				("b-stream3", none),
				("a-stream4", new[] { "a-stream4" }),
				("b-stream3", none), // 1c
				("a-stream1", none), // 1b
				("a-stream2", none)); // 1a

			object[] Case(string name, params (string, string[])[] data) {
				return new object[] {
					name, data
				};
			}
		}

		[Theory]
		[MemberData(nameof(TheCases))]
		public void Works(string caseName, (string StreamName, string[] NewCollisions)[] data) {
			Assert.NotNull(caseName);

			var log = data.Select(x => x.StreamName).ToArray();
			var hasher = new FirstCharacterHasher();

			// index maps hashes to lists of log positions
			var index = new Dictionary<ulong, List<int>>();

			// populate the index
			for (var i = 0; i < log.Length; i++) {
				var streamName = log[i];
				var hash = hasher.Hash(streamName);
				if (!index.TryGetValue(hash, out var entries)) {
					entries = new List<int>();
					index[hash] = entries;
				}

				// which order is more realistic? this way shows the need for filtering by position anyway
				entries.Insert(0, i);
			}

			var hashes = new SqliteScavengeMap<ulong, string>("hashes");
			hashes.Initialize(new SqliteBackend(Fixture.DbConnection));

			var collisions = new SqliteFixedStructScavengeMap<string, Unit>("collisions");
			collisions.Initialize(new SqliteBackend(Fixture.DbConnection));

			var sut = new CollisionDetector<string>(
				new LruCachingScavengeMap<ulong, string>(
					hashes,
					cacheMaxCount: 1000),
				collisions,
				hasher);

			var expectedCollisions = new HashSet<string>();

			for (var i = 0; i < data.Length; i++) {
				foreach (var newCollision in data[i].NewCollisions)
					expectedCollisions.Add(newCollision);

				sut.DetectCollisions(data[i].StreamName, out _);
				Assert.Equal(
					expectedCollisions.OrderBy(x => x),
					sut.AllCollisions());
			}
		}
	}
}
