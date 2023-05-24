using System;
using System.Text;
using System.Threading;
using Xunit;
using Xunit.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Newtonsoft.Json.Linq;

namespace ResgateIO.Service.UnitTests
{
    public class ResServiceTests : TestsBase
    {
        public ResServiceTests(ITestOutputHelper output) : base(output) { }

        [Fact]
        public void ProtocolVersion_ReturnsCurrentVersion()
        {
            Assert.Equal("1.2.2", Service.ProtocolVersion);
        }

        [Fact]
        public void Serve_NoException()
        {
            Service.Serve(Conn);
        }

        [Fact]
        public void Serve_NoRegisteredHandlers_NoSystemReset()
        {
            Service.Serve(Conn);
            Conn.AssertNoMsg();
        }

        [Fact]
        public void Serve_OwnedResourcesSet_SendsSystemReset()
        {
            var resources = new string[] { "test.>" };
            var access = new string[] { "test.foo.>" };
            Service.SetOwnedResources(resources, access);
            Service.Serve(Conn);
            Conn.GetMsg()
                .AssertSubject("system.reset")
                .AssertPayload(new { resources, access });
        }

        [Fact]
        public void Serve_RegisteredGetHandler_SendsResourcesInSystemReset()
        {
            Service.AddHandler("model", new DynamicHandler().ModelGet(r => r.NotFound()));
            Service.Serve(Conn);
            Conn.GetMsg()
                .AssertSubject("system.reset")
                .AssertPayload(new { resources = new string[] { "test.>" }, access = new string[] { } });
        }

        [Fact]
        public void Serve_RegisteredAccessHandler_SendsAccessInSystemReset()
        {
            Service.AddHandler("model", new DynamicHandler().Access(r => r.AccessDenied()));
            Service.Serve(Conn);
            Conn.GetMsg()
                .AssertSubject("system.reset")
                .AssertPayload(new { resources = new string[] { }, access = new string[] { "test.>" } });
        }

        [Fact]
        public void Serve_RegisteredAccessAndGetHandler_SendsResourceAndAccessInSystemReset()
        {
            Service.AddHandler("model", new DynamicHandler()
                .Access(r => r.AccessDenied())
                .Get(r => r.NotFound()));
            Service.Serve(Conn);
            Conn.GetMsg()
                .AssertSubject("system.reset")
                .AssertPayload(new { resources = new string[] { "test.>" }, access = new string[] { "test.>" } });
        }

        [Fact]
        public void Shutdown_GetHandler_RemovesSubscriptions()
        {
            Service.AddHandler("model", new DynamicHandler().Get(r => r.Model(Test.Model)));
            Assert.Equal(0, Conn.SubscriptionCount);
            Service.Serve(Conn);
            Assert.Equal(3, Conn.SubscriptionCount);
            Service.Shutdown();
            Assert.Equal(0, Conn.SubscriptionCount);
        }

        [Fact]
        public void Shutdown_AccessHandler_RemovesSubscriptions()
        {
            Service.AddHandler("model", new DynamicHandler().Access(r => r.AccessGranted()));
            Assert.Equal(0, Conn.SubscriptionCount);
            Service.Serve(Conn);
            Assert.Equal(1, Conn.SubscriptionCount);
            Service.Shutdown();
            Assert.Equal(0, Conn.SubscriptionCount);
        }

        [Fact]
        public void Shutdown_GetAndAccessHandler_RemovesSubscriptions()
        {
            Service.AddHandler("model", new DynamicHandler()
                .Access(r => r.AccessGranted())
                .Get(r => r.Model(Test.Model)));
            Assert.Equal(0, Conn.SubscriptionCount);
            Service.Serve(Conn);
            Assert.Equal(4, Conn.SubscriptionCount);
            Service.Shutdown();
            Assert.Equal(0, Conn.SubscriptionCount);
        }

        [Fact]
        public void SetLogger_NullParameter_NoException()
        {
            Service.SetLogger(null);
            Service.Serve(Conn);
        }

        [Fact]
        public void TokenEvent_WithToken_SendsTokenEvent()
        {
            Service.Serve(Conn);
            Service.TokenEvent(Test.CID, Test.Token);
            Conn.GetMsg()
                .AssertSubject("conn." + Test.CID + ".token")
                .AssertPayload(new { token = Test.Token });
        }

        [Fact]
        public void TokenEvent_WithNullToken_SendsNullTokenEvent()
        {
            Service.Serve(Conn);
            Service.TokenEvent(Test.CID, null);
            Conn.GetMsg()
                .AssertSubject("conn." + Test.CID + ".token")
                .AssertPayload(new { token = (object)null });
        }

        [Fact]
        public void TokenEvent_WithInvalidCID_ThrowsException()
        {
            Service.Serve(Conn);
            Assert.Throws<ArgumentException>(() => Service.TokenEvent("invalid.*.cid", null));
        }

        [Fact]
        public void TokenEvent_WithTokenID_SendsTokenEventWithTID()
        {
            Service.Serve(Conn);
            Service.TokenEvent(Test.CID, Test.Token, Test.TID);
            Conn.GetMsg()
                .AssertSubject("conn." + Test.CID + ".token")
                .AssertPayload(new { token = Test.Token, tid = Test.TID });
        }

        [Fact]
        public void TokenEvent_WithNullToken_SendsNullTokenEventWithTID()
        {
            Service.Serve(Conn);
            Service.TokenEvent(Test.CID, null, Test.TID);
            Conn.GetMsg()
                .AssertSubject("conn." + Test.CID + ".token")
                .AssertPayload(new { token = (object)null, tid = Test.TID });
        }

        [Theory]
        [InlineData("auth", new string[] { "foo" }, "{\"tids\":[\"foo\"],\"subject\":\"auth\"}")]
        [InlineData("auth", new string[] { "foo", "bar" }, "{\"tids\":[\"foo\",\"bar\"],\"subject\":\"auth\"}")]
        [InlineData("auth.test.method", new string[] { "foo", "bar" }, "{\"tids\":[\"foo\",\"bar\"],\"subject\":\"auth.test.method\"}")]
        public void TokenReset_WithTokenIDs_SendsSystemTokenResetEvent(string subject, string[] tids, string expectedJson)
        {
            Service.Serve(Conn);
            Service.TokenReset(subject, tids);
            Conn.GetMsg()
                .AssertSubject("system.tokenReset")
                .AssertPayload(JObject.Parse(expectedJson));
        }

        [Theory]
        [InlineData("auth", null)]
        [InlineData("auth", new string[] { })]
        public void TokenReset_WithNoTokenIDs_DoesNotSendsSystemTokenResetEvent(string subject, string[] tids)
        {
            Service.Serve(Conn);
            Service.TokenReset(subject, tids);
            Conn.AssertNoMsg();
        }

        [Fact]
        public void With_WithValidResourceID_CallsCallback()
        {
            AutoResetEvent ev = new AutoResetEvent(false);
            Service.AddHandler("model", new DynamicHandler().Get(r => r.NotFound()));
            Service.Serve(Conn);
            Service.With("test.model", r => ev.Set());
            Assert.True(ev.WaitOne(Test.TimeoutDuration), "callback was not called before timeout");
        }

        [Fact]
        public void With_WithoutMatchingPattern_ThrowsException()
        {
            Service.Serve(Conn);
            Assert.Throws<ArgumentException>(() =>
            {
                Service.With("test.model", r => { });
            });
        }
        
        [Fact]
        public void With_UsingResource_ThrowsException()
        {
            AutoResetEvent ev = new AutoResetEvent(false);
            Service.AddHandler("model", new DynamicHandler().Get(r => r.NotFound()));
            Service.Serve(Conn);
            var resource = Service.Resource("test.model");
            Service.With(resource, () => ev.Set());
            Assert.True(ev.WaitOne(Test.TimeoutDuration), "callback was not called before timeout");
        }

        [Fact]
        public void With_UsingResourceWithCallbackResourceParam_ThrowsException()
        {
            AutoResetEvent ev = new AutoResetEvent(false);
            Service.AddHandler("model", new DynamicHandler().Get(r => r.NotFound()));
            Service.Serve(Conn);
            var resource = Service.Resource("test.model");
            Service.With(resource, r =>
            {
                Assert.Equal(resource, r);
                ev.Set();
            });
            Assert.True(ev.WaitOne(Test.TimeoutDuration), "callback was not called before timeout");
        }

        [Fact]
        public void WithGroup_WithMatchingResource_CallsCallback()
        {
            AutoResetEvent ev = new AutoResetEvent(false);
            Service.AddHandler("model", "mygroup", new DynamicHandler().Get(r => r.NotFound()));
            Service.Serve(Conn);
            Service.WithGroup("mygroup", () => ev.Set());
            Assert.True(ev.WaitOne(Test.TimeoutDuration), "callback was not called before timeout");
        }

        [Fact]
        public void WithGroup_WithMatchingResourceWithCallbackServiceParam_CallsCallback()
        {
            AutoResetEvent ev = new AutoResetEvent(false);
            Service.AddHandler("model", "mygroup", new DynamicHandler().Get(r => r.NotFound()));
            Service.Serve(Conn);
            Service.WithGroup("mygroup", s =>
            {
                Assert.Equal(Service, s);
                ev.Set();
            });
            Assert.True(ev.WaitOne(Test.TimeoutDuration), "callback was not called before timeout");
        }

        [Fact]
        public void Serving_OnServe_EventHandlerCalled()
        {
            int called = 0;
            Service.Serving += (sender, e) => called++;
            Service.Serve(Conn);
            Assert.Equal(1, called);
        }

        [Fact]
        public void Stopped_OnStopped_EventHandlerCalled()
        {
            int called = 0;
            Service.Stopped += (sender, e) => called++;
            Service.Serve(Conn);
            Assert.Equal(0, called);
            Service.Shutdown();
            Assert.Equal(1, called);
        }

        [Fact]
        public void Stopped_OnError_EventHandlerCalled()
        {
            int called = 0;
            Service.Error += (sender, e) => called++;
            Service.AddHandler("model", new DynamicHandler().ModelGet(r => r.Model(Test.Model)));
            Conn.FailNextSubscription();
            Assert.Equal(0, called);
            Assert.Throws<Exception>(() => Service.Serve(Conn));
            Assert.Equal(1, called);
        }

        [Fact]
        public void SetSerializationSettings_OnResponse_EncodesWithSettings()
        {
            Service.SetSerializerSettings(new JsonSerializerSettings
            {
                ContractResolver = new DefaultContractResolver
                {
                    NamingStrategy = new CamelCaseNamingStrategy()
                }
            });
            Service.AddHandler("model", new DynamicHandler().Call(r => r.Ok(new { FooBar = "bar" })));
            Service.Serve(Conn);
            Conn.GetMsg().AssertSubject("system.reset");
            string inbox = Conn.NATSRequest("call.test.model.method", Test.Request);
            Conn.GetMsg()
                .AssertSubject(inbox)
                .AssertPayload(Encoding.UTF8.GetBytes(@"{""result"":{""fooBar"":""bar""}}"));
        }

        [Fact]
        public void SetOwnedResources_WithoutFullWildcard_SubscribesToGetRequests()
        {
            var resources = new string[] { "test.model.*" };
            Service.SetOwnedResources(resources, null);
            Service.AddHandler("model.$id", new DynamicHandler().Get(r => r.Model(Test.Model)));
            Service.Serve(Conn);
            Conn.GetMsg().AssertSubject("system.reset");

            string inbox = Conn.NATSRequest("get.test.model.42", Test.EmptyRequest);
            Conn.GetMsg()
                .AssertSubject(inbox)
                .AssertResult(new { model = Test.Model });
        }

        [Fact]
        public void SetOwnedResources_WithoutFullWildcard_SubscribesToCallRequests()
        {
            var resources = new string[] { "test.model.*" };
            Service.SetOwnedResources(resources, null);
            Service.AddHandler("model.$id", new DynamicHandler().Call(r => r.Ok(Test.Result)));
            Service.Serve(Conn);
            Conn.GetMsg().AssertSubject("system.reset");

            string inbox = Conn.NATSRequest("call.test.model.42.method", Test.Request);
            Conn.GetMsg()
                .AssertSubject(inbox)
                .AssertResult(Test.Result);
        }

        [Fact]
        public void SetOwnedResources_WithoutFullWildcard_SubscribesToAuthRequests()
        {
            var resources = new string[] { "test.model.*" };
            Service.SetOwnedResources(resources, null);
            Service.AddHandler("model.$id", new DynamicHandler().Auth(r => r.Ok(Test.Result)));
            Service.Serve(Conn);
            Conn.GetMsg().AssertSubject("system.reset");

            string inbox = Conn.NATSRequest("auth.test.model.42.method", Test.Request);
            Conn.GetMsg()
                .AssertSubject(inbox)
                .AssertResult(Test.Result);
        }

        [Fact]
        public void SetErrorHandler_NullParameter_NoException()
        {
            Service.SetGlobalRequestErrorHandler(null);
            Service.Serve(Conn);
        }
    }
}
