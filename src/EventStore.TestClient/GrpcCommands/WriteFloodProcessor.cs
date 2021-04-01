using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.TestClient.Commands;

namespace EventStore.TestClient.GrpcCommands {
	internal class WriteFloodProcessor : ICmdProcessor {
		private static readonly UTF8Encoding UTF8NoBom = new UTF8Encoding(false);

		public string Usage {
			get { return "WRFLGRPC [<clients> <requests> [<streams-cnt> [<size>] [<batchsize>]]]"; }
		}

		public string Keyword {
			get { return "WRFLGRPC"; }
		}

		public bool Execute(CommandProcessorContext context, string[] args) {
			int clientsCnt = 1;
			long requestsCnt = 5000;
			int streamsCnt = 1000;
			int size = 256;
			int batchSize = 1;
			if (args.Length > 0)
			{
			    if (args.Length < 2 || args.Length > 5)
			        return false;
			
			    try
			    {
			        clientsCnt = int.Parse(args[0]);
			        requestsCnt = long.Parse(args[1]);
			        if (args.Length >= 3)
			            streamsCnt = int.Parse(args[2]);
			        if (args.Length >= 4)
			            size = int.Parse(args[3]);
			        if (args.Length >= 5)
			            batchSize = int.Parse(args[4]);
			    }
			    catch
			    {
			        return false;
			    }
			}
			
			var monitor = new RequestMonitor();
			try {
				var task = WriteFlood(context, clientsCnt, requestsCnt, streamsCnt, size, batchSize, monitor);
				task.Wait();
			} catch (Exception ex) {
				context.Fail(ex);
			}

			return true;
		}

		private async Task WriteFlood(CommandProcessorContext context, int clientsCnt, long requestsCnt, int streamsCnt,
			int size, int batchSize, RequestMonitor monitor) {
			context.IsAsync();

			long succ = 0;
			long last = 0;
			long fail = 0;
			long prepTimeout = 0;
			long commitTimeout = 0;
			long forwardTimeout = 0;
			long wrongExpVersion = 0;
			long streamDeleted = 0;
			long all = 0;
			long interval = 100000;
			long currentInterval = 0;

			var deterministicStreamNames = true;
			var deterministicStreamSelection = true;
			var streams = Enumerable
				.Range(0, streamsCnt)
				.Select(x => deterministicStreamNames
					? $"{x}.{Guid.Empty}"
					: Guid.NewGuid().ToString())
				.ToArray();
			Console.WriteLine($"Last stream: {streams.LastOrDefault()}");

			var start = new TaskCompletionSource();
			var sw2 = new Stopwatch();
			var capacity = 2000 / clientsCnt;
			var clientTasks = new List<Task>();
			for (int i = 0; i < clientsCnt; i++) {
				var count = requestsCnt / clientsCnt + ((i == clientsCnt - 1) ? requestsCnt % clientsCnt : 0);

				var client = context._grpcTestClient.CreateGrpcClient();
				clientTasks.Add(RunClient(i, client, count));
			}

			async Task RunClient(int clientNum, EventStoreClient client, long count) {
				var rnd = new Random();
				List<Task> pending = new List<Task>(capacity);
				await start.Task;
				int k = (streamsCnt / clientsCnt) * clientNum;
				if (deterministicStreamSelection)
					Console.WriteLine($"Writer {clientNum} writing {count} writes starting at stream {k}");
				for (int j = 0; j < count; ++j) {

					var events = new EventData[batchSize];
					for (int q = 0; q < batchSize; q++) {
						events[q] = new EventData(Uuid.FromGuid(Guid.NewGuid()),
							"TakeSomeSpaceEvent",
							UTF8NoBom.GetBytes(
								"{ \"DATA\" : \"" + new string('*', size) + "\"}"),
							UTF8NoBom.GetBytes(
								"{ \"METADATA\" : \"" + new string('$', 100) + "\"}"));
					}

					var corrid = Guid.NewGuid();
					monitor.StartOperation(corrid);

					var streamIndex = rnd.Next(streamsCnt);
					if (deterministicStreamSelection) {
						streamIndex = k++;
						if (k >= streamsCnt)
							k = 0;
					}

					pending.Add(client.AppendToStreamAsync(streams[streamIndex], StreamState.Any, events)
						.ContinueWith(t => {
							if (t.IsCompletedSuccessfully) Interlocked.Increment(ref succ);
							else {
								if (Interlocked.Increment(ref fail) % 1000 == 0)
									Console.Write('#');
							}
							var localAll = Interlocked.Add(ref all, batchSize);
							if (localAll - currentInterval > interval) {
								var localInterval = Interlocked.Exchange(ref currentInterval, localAll);
								var elapsed = sw2.Elapsed;
								sw2.Restart();
								context.Log.Information(
									"\nDONE TOTAL {writes} WRITES IN {elapsed} ({rate:0.0}/s) [S:{success}, F:{failures} (WEV:{wrongExpectedVersion}, P:{prepareTimeout}, C:{commitTimeout}, F:{forwardTimeout}, D:{streamDeleted})].",
									localAll, elapsed, 1000.0 * (localAll - localInterval) / elapsed.TotalMilliseconds,
									succ, fail,
									wrongExpVersion, prepTimeout, commitTimeout, forwardTimeout, streamDeleted);
							}

							monitor.EndOperation(corrid);
						}));
					if (pending.Count == capacity) {
						await Task.WhenAny(pending).ConfigureAwait(false);

						while (pending.Count > 0 && Task.WhenAny(pending).IsCompleted) {
							pending.RemoveAll(x => x.IsCompleted);
							if (succ - last > 1000) {
								Console.Write(".");
								last = succ;
							}
						}
					}
				}

				if (pending.Count > 0) await Task.WhenAll(pending);
			}

			var sw = Stopwatch.StartNew();
			sw2.Start();
			start.SetResult();
			await Task.WhenAll(clientTasks);
			sw.Stop();

			context.Log.Information(
				"Completed. Successes: {success}, failures: {failures} (WRONG VERSION: {wrongExpectedVersion}, P: {prepareTimeout}, C: {commitTimeout}, F: {forwardTimeout}, D: {streamDeleted})",
				succ, fail,
				wrongExpVersion, prepTimeout, commitTimeout, forwardTimeout, streamDeleted);

			var reqPerSec = (all + 0.0) / sw.ElapsedMilliseconds * 1000;
			context.Log.Information("{requests} requests completed in {elapsed}ms ({rate:0.00} reqs per sec).", all,
				sw.ElapsedMilliseconds, reqPerSec);

			PerfUtils.LogData(
				Keyword,
				PerfUtils.Row(PerfUtils.Col("clientsCnt", clientsCnt),
					PerfUtils.Col("requestsCnt", requestsCnt),
					PerfUtils.Col("ElapsedMilliseconds", sw.ElapsedMilliseconds)),
				PerfUtils.Row(PerfUtils.Col("successes", succ), PerfUtils.Col("failures", fail)));

			var failuresRate = (int)(100 * fail / (fail + succ));
			PerfUtils.LogTeamCityGraphData(string.Format("{0}-{1}-{2}-reqPerSec", Keyword, clientsCnt, requestsCnt),
				(int)reqPerSec);
			PerfUtils.LogTeamCityGraphData(
				string.Format("{0}-{1}-{2}-failureSuccessRate", Keyword, clientsCnt, requestsCnt), failuresRate);
			PerfUtils.LogTeamCityGraphData(
				string.Format("{0}-c{1}-r{2}-st{3}-s{4}-reqPerSec", Keyword, clientsCnt, requestsCnt, streamsCnt, size),
				(int)reqPerSec);
			PerfUtils.LogTeamCityGraphData(
				string.Format("{0}-c{1}-r{2}-st{3}-s{4}-failureSuccessRate", Keyword, clientsCnt, requestsCnt,
					streamsCnt, size), failuresRate);
			monitor.GetMeasurementDetails();
			if (Interlocked.Read(ref succ) != requestsCnt)
				context.Fail(reason: "There were errors or not all requests completed.");
			else
				context.Success();
		}
	}
}