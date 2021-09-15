# EventType

When you append to a stream and don't the `application/vnd.eventstore.events+json/+xml` media type you must specify an event type with the event that you are posting. This isn't required with the custom media type as it's specified within the format itself.

You use the `ES-EventType` header as follows.

:::: code-group
::: code Request
<<< @/samples/http-api/append-event-to-new-stream.sh#curl
:::
::: code Response
<<< @/samples/http-api/append-event-to-new-stream.sh#response
:::
::::

If you view the event in the UI or with cURL it has the `EventType` of `SomeEvent`:

<!-- TODO: Does this make sense? If I can't use the custom media type -->

:::: code-group
::: code Request
<<< @/samples/http-api/read-event.sh#curl
:::
::: code Response
<<< @/samples/http-api/read-event.sh#response
:::
::::