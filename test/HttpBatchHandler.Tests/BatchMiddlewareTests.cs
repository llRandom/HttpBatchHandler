﻿using HttpBatchHandler.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using NSubstitute;
using NSubstitute.Core;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace HttpBatchHandler.Tests
{
    public class BatchMiddlewareTests : BaseWriterTests, IClassFixture<BatchMiddlewareTestFixture>
    {
        public BatchMiddlewareTests(BatchMiddlewareTestFixture fixture)
        {
            _fixture = fixture;
        }

        private readonly BatchMiddlewareTestFixture _fixture;

        private static string GetTextFromStream(Stream stream)
        {
            string result;
            Assert.NotNull(stream);
            using (stream)
            {
                using (var tr = new StreamReader(stream))
                {
                    result = tr.ReadToEnd();
                }
            }
            return result;
        }

        private async Task AssertExecution(IHttpRequestFeature requestFeature, IHttpResponseFeature responseFeature,
            BatchMiddlewareEvents batchMiddlewareEvents,
            params ResponseFeature[] responseFeatures)
        {
            var featureCollection = new FeatureCollection();
            featureCollection.Set(requestFeature);
            featureCollection.Set(responseFeature);
            var defaultContext = new DefaultHttpContext(featureCollection);
            var middleware = CreateMiddleware(CreateRequestDelegate(responseFeatures), batchMiddlewareEvents);
            await middleware.Invoke(defaultContext).ConfigureAwait(false);
        }


        private RequestDelegate CreateRequestDelegate(ResponseFeature[] responseFeatures)
        {
            var requestDelegate = Substitute.For<RequestDelegate>();
            var functorArray = new Func<CallInfo, Task>[responseFeatures.Length];
            for (var i = 0; i < responseFeatures.Length; i++)
            {
                var index = i;
                functorArray[i] = ci => ReturnThis(ci.Arg<HttpContext>(), responseFeatures[index]);
            }
            requestDelegate.Invoke(null).ReturnsForAnyArgs(functorArray.First(), functorArray.Skip(1).ToArray());
            return requestDelegate;
        }

        private Task ReturnThis(HttpContext context, ResponseFeature responseFeature)
        {
            context.Response.Body = responseFeature.Body;
            foreach (var kval in responseFeature.Headers)
            {
                context.Response.Headers.Add(kval);
            }
            context.Response.StatusCode = responseFeature.StatusCode;
            context.Response.HttpContext.Features.Get<IHttpResponseFeature>().ReasonPhrase =
                responseFeature.ReasonPhrase;
            return Task.CompletedTask;
        }


        private BatchMiddleware CreateMiddleware(RequestDelegate next, BatchMiddlewareEvents eventHandler)
        {
            return new BatchMiddleware(next, new MockHttpContextFactory(),
                new BatchMiddlewareOptions {Events = eventHandler});
        }

        private class MockHttpContextFactory : IHttpContextFactory
        {
            public HttpContext Create(IFeatureCollection featureCollection)
            {
                return new DefaultHttpContext(featureCollection);
            }

            public void Dispose(HttpContext httpContext)
            {
            }
        }


        private class ThrowExceptionEventHandler : MockedBatchEventHandler
        {

            public override Task BatchRequestExecuted(BatchRequestExecutedContext context)
            {
                if (BatchRequestExecutedCount == 2)
                {
                    throw new InvalidOperationException();
                }
                return base.BatchRequestExecuted(context);
            }
        }


        private class MockedBatchEventHandler : BatchMiddlewareEvents
        {
            public int BatchEndCount { get; private set; }
            public int BatchRequestExecutedCount { get; private set; }
            public int BatchRequestExecutingCount { get; private set; }
            public int BatchStartCount { get; private set; }
            public int BatchRequestPreparationCount { get; private set; }

            private readonly Guid _state = Guid.NewGuid();

            public override async Task BatchEnd(BatchEndContext context)
            {
                BatchEndCount += 1;
                if (context.IsAborted)
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                }
                if (context.Exception != null)
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    context.Response.Headers.Add(HeaderNames.ContentType, "text/plain");
                    context.IsHandled = true;
                    await context.Response.WriteAsync("Something went wrong.");
                }
                Assert.Equal(_state, context.State);
                await base.BatchEnd(context);
            }

            public override Task BatchRequestPreparation(BatchRequestPreparationContext context)
            {
                BatchRequestPreparationCount += 1;
                Assert.Equal(_state, context.State);
                return base.BatchRequestPreparation(context);
            }

            public override Task BatchRequestExecuted(BatchRequestExecutedContext context)
            {
                BatchRequestExecutedCount += 1;
                if (!context.Response.IsSuccessStatusCode())
                {
                    context.Abort = true;
                    context.Exception = new InvalidOperationException();
                }
                Assert.Equal(_state, context.State);
                return base.BatchRequestExecuted(context);
            }

            public override Task BatchRequestExecuting(BatchRequestExecutingContext context)
            {
                BatchRequestExecutingCount += 1;
                Assert.Equal(_state, context.State);
                return base.BatchRequestExecuting(context);
            }

            public override Task BatchStart(BatchStartContext context)
            {
                BatchStartCount += 1;
                context.State = _state;
                return base.BatchStart(context);
            }
        }

        [Fact]
        public async Task BadContentType()
        {
            var requestFeature = new HttpRequestFeature {Path = "/api/batch"};
            requestFeature.Headers.Add(HeaderNames.ContentType, "application/json");
            requestFeature.Body = Stream.Null;
            var responseFeature = new HttpResponseFeature();
            var mockedEvents = new MockedBatchEventHandler();
            using (responseFeature.Body = new MemoryStream())
            {
                await AssertExecution(requestFeature, responseFeature, mockedEvents,
                    CreateFirstResponse(),
                    CreateSecondResponse(),
                    CreateThirdResponse(),
                    CreateFourthResponse()).ConfigureAwait(false);
                Assert.Equal(StatusCodes.Status400BadRequest, responseFeature.StatusCode);
                Assert.Equal(0, mockedEvents.BatchStartCount);
            }
        }

        [Fact]
        public async Task CompareRequestResponse()
        {
            var requestFeature = new HttpRequestFeature {Path = "/api/batch"};
            requestFeature.Headers.Add(HeaderNames.ContentType,
                "multipart/mixed; boundary=\"batch_357647d1-a6b5-4e6a-aa73-edfc88d8866e\"");
            requestFeature.Body = GetType().Assembly
                .GetManifestResourceStream(typeof(MultipartParserTests), "MultipartRequest.txt");
            var responseFeature = new HttpResponseFeature();
            var mockedEvents = new MockedBatchEventHandler();
            using (responseFeature.Body = new MemoryStream())
            {
                await AssertExecution(requestFeature, responseFeature, mockedEvents,
                    CreateFirstResponse(),
                    CreateSecondResponse(),
                    CreateThirdResponse(),
                    CreateFourthResponse()).ConfigureAwait(false);
                Assert.Equal(StatusCodes.Status200OK, responseFeature.StatusCode);
                var refText = GetTextFromStream(GetType().Assembly
                    .GetManifestResourceStream(typeof(MultipartParserTests), "MultipartResponse.txt"));
                responseFeature.Body.Position = 0;
                var outputText = GetTextFromStream(responseFeature.Body);
                var boundary = Regex.Match(outputText, "--(.+?)--").Groups[1].Value;
                refText = refText.Replace("61cfbe41-7ea6-4771-b1c5-b43564208ee5",
                    boundary); // replace with current boundary;
                Assert.Equal(refText, outputText);
                Assert.Equal(1, mockedEvents.BatchEndCount);
                Assert.Equal(1, mockedEvents.BatchStartCount);
                Assert.Equal(4, mockedEvents.BatchRequestPreparationCount);
                Assert.Equal(4, mockedEvents.BatchRequestExecutingCount);
                Assert.Equal(4, mockedEvents.BatchRequestExecutedCount);
            }
        }

        [Fact]
        public async Task AbortNonSuccessStatusCode()
        {
            var requestFeature = new HttpRequestFeature { Path = "/api/batch" };
            requestFeature.Headers.Add(HeaderNames.ContentType,
                "multipart/mixed; boundary=\"batch_357647d1-a6b5-4e6a-aa73-edfc88d8866e\"");
            requestFeature.Body = GetType().Assembly
                .GetManifestResourceStream(typeof(MultipartParserTests), "MultipartRequest.txt");
            var responseFeature = new HttpResponseFeature();
            var mockedEvents = new MockedBatchEventHandler();
            using (responseFeature.Body = new MemoryStream())
            {
                await AssertExecution(requestFeature, responseFeature, mockedEvents,
                    CreateFirstResponse(),
                    CreateInternalServerResponse(),
                    CreateThirdResponse(),
                    CreateFourthResponse()).ConfigureAwait(false);
                Assert.Equal(StatusCodes.Status500InternalServerError, responseFeature.StatusCode);
                Assert.Equal(1, mockedEvents.BatchEndCount);
                Assert.Equal(1, mockedEvents.BatchStartCount);
                Assert.Equal(2, mockedEvents.BatchRequestPreparationCount);
                Assert.Equal(2, mockedEvents.BatchRequestExecutingCount);
                Assert.Equal(2, mockedEvents.BatchRequestExecutedCount);
            }
        }

        [Fact]
        public async Task AbortException()
        {
            var requestFeature = new HttpRequestFeature { Path = "/api/batch" };
            requestFeature.Headers.Add(HeaderNames.ContentType,
                "multipart/mixed; boundary=\"batch_357647d1-a6b5-4e6a-aa73-edfc88d8866e\"");
            requestFeature.Body = GetType().Assembly
                .GetManifestResourceStream(typeof(MultipartParserTests), "MultipartRequest.txt");
            var responseFeature = new HttpResponseFeature();
            var mockedEvents = new ThrowExceptionEventHandler();
            using (responseFeature.Body = new MemoryStream())
            {
                await AssertExecution(requestFeature, responseFeature, mockedEvents,
                    CreateFirstResponse(),
                    CreateSecondResponse(),
                    CreateThirdResponse(),
                    CreateFourthResponse()).ConfigureAwait(false);
                Assert.Equal(StatusCodes.Status500InternalServerError, responseFeature.StatusCode);
                Assert.Equal(1, mockedEvents.BatchEndCount);
                Assert.Equal(1, mockedEvents.BatchStartCount);
                Assert.Equal(3, mockedEvents.BatchRequestPreparationCount);
                Assert.Equal(3, mockedEvents.BatchRequestExecutingCount);
                Assert.Equal(2, mockedEvents.BatchRequestExecutedCount);
            }
        }


        [Fact]
        public async Task NoBoundary()
        {
            var requestFeature = new HttpRequestFeature {Path = "/api/batch"};
            requestFeature.Headers.Add(HeaderNames.ContentType, "multipart/mixed");
            requestFeature.Body = Stream.Null;
            var responseFeature = new HttpResponseFeature();
            var mockedEvents = new MockedBatchEventHandler();
            using (responseFeature.Body = new MemoryStream())
            {
                await AssertExecution(requestFeature, responseFeature, mockedEvents,
                    CreateFirstResponse(),
                    CreateSecondResponse(),
                    CreateThirdResponse(),
                    CreateFourthResponse()).ConfigureAwait(false);
                Assert.Equal(StatusCodes.Status400BadRequest, responseFeature.StatusCode);
                Assert.Equal(0, mockedEvents.BatchStartCount);
            }
        }
    }
}