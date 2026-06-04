using System;
using System.Linq;
using System.Net;
using SynapseSocket.Core;
using SynapseSocket.Security;
using Xunit;
using SynapseSocket.Core.Events;

namespace SynapseSocket.Tests.Security;

public class SignatureValidatorTests
{
    private sealed class AcceptAllValidator : ISignatureValidator
    {
        public int Calls;
        public bool Validate(IPEndPoint endPoint, ulong signature, ReadOnlySpan<byte> handshakePayload)
        {
            Calls++;
            return true;
        }
    }

    private sealed class RejectAllValidator : ISignatureValidator
    {
        public bool Validate(IPEndPoint endPoint, ulong signature, ReadOnlySpan<byte> handshakePayload) => false;
    }

    private sealed class FixedSignatureProvider : ISignatureProvider
    {
        public readonly ulong Value;
        public int Calls;
        public FixedSignatureProvider(ulong v) { Value = v; }
        public bool TryCompute(IPEndPoint endPoint, ReadOnlySpan<byte> handshakePayload, out ulong signature)
        {
            Calls++;
            signature = Value;
            return true;
        }
    }

    [Fact]
    public void AcceptAll_Validator_Is_Invoked_And_Connection_Completes()
    {
        int port = TestHarness.GetFreePort();
        AcceptAllValidator validator = new();

        using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.Security.SignatureValidator = validator;
        }));
        using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder eventRecorder = new();
        eventRecorder.Attach(server);

        server.Start();
        client.Start();

        client.Connect(new(IPAddress.Loopback, port));
        Assert.True(TestHarness.PumpUntil(() => eventRecorder.ConnectionsEstablished >= 1, 2000, server, client));
        Assert.True(validator.Calls >= 1, "custom validator should be called at least once");
    }

    [Fact]
    public void RejectAll_Validator_Blocks_Handshake_And_Raises_SignatureRejected()
    {
        int port = TestHarness.GetFreePort();

        using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.Security.SignatureValidator = new RejectAllValidator();
        }));
        using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder eventRecorder = new();
        eventRecorder.Attach(server);

        server.Start();
        client.Start();
        client.Connect(new(IPAddress.Loopback, port));

        Assert.True(TestHarness.PumpUntil(
            () => eventRecorder.FailureReasons.Contains(ConnectionRejectedReason.SignatureRejected), 2000, server, client));
        Assert.Equal(0, eventRecorder.ConnectionsEstablished);
    }

    [Fact]
    public void Custom_SignatureProvider_Is_Used_For_Signature_Calculation()
    {
        int port = TestHarness.GetFreePort();
        FixedSignatureProvider provider = new(0xCAFEBABEDEADBEEF);

        using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.Security.SignatureProvider = provider;
        }));
        using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder eventRecorder = new();
        eventRecorder.Attach(server);

        server.Start();
        client.Start();
        client.Connect(new(IPAddress.Loopback, port));
        TestHarness.PumpUntil(() => eventRecorder.ConnectionsEstablished >= 1, 2000, server, client);

        // Server should have created the connection with the FIXED signature.
        Assert.True(server.Connections.ConnectionsBySignature.TryGetValue(0xCAFEBABEDEADBEEF, out Connections.SynapseConnection? foundSynapseConnection));
        Assert.NotNull(foundSynapseConnection);
        Assert.True(provider.Calls > 0);
    }
}
