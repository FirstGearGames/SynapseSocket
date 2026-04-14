using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SynapseSocket.Core;
using SynapseSocket.Security;
using Xunit;
using SynapseSocket.Core.Events;

namespace SynapseSocket.Tests;

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
    public async Task AcceptAll_Validator_Is_Invoked_And_Connection_Completes()
    {
        int port = TestHarness.GetFreePort();
        AcceptAllValidator validator = new();

        await using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.SignatureValidator = validator;
        }));
        await using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder eventRecorder = new();
        eventRecorder.Attach(server);

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        Assert.True(await TestHarness.WaitFor(() => eventRecorder.ConnectionsEstablished >= 1));
        Assert.True(validator.Calls >= 1, "custom validator should be called at least once");
    }

    [Fact]
    public async Task RejectAll_Validator_Blocks_Handshake_And_Raises_SignatureRejected()
    {
        int port = TestHarness.GetFreePort();

        await using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.SignatureValidator = new RejectAllValidator();
        }));
        await using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder eventRecorder = new();
        eventRecorder.Attach(server);

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);
        await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);

        Assert.True(await TestHarness.WaitFor(
            () => eventRecorder.FailureReasons.Contains(ConnectionRejectedReason.SignatureRejected)));
        Assert.Equal(0, eventRecorder.ConnectionsEstablished);
    }

    [Fact]
    public async Task Custom_SignatureProvider_Is_Used_For_Signature_Calculation()
    {
        int port = TestHarness.GetFreePort();
        FixedSignatureProvider provider = new(0xCAFEBABEDEADBEEF);

        await using SynapseManager server = new(TestHarness.ServerConfig(port, c =>
        {
            c.SignatureProvider = provider;
        }));
        await using SynapseManager client = new(TestHarness.ClientConfig());

        TestHarness.EventRecorder eventRecorder = new();
        eventRecorder.Attach(server);

        await server.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);
        Connections.SynapseConnection _ = await client.ConnectAsync(new(IPAddress.Loopback, port), CancellationToken.None);
        await TestHarness.WaitFor(() => eventRecorder.ConnectionsEstablished >= 1);

        // Server should have created the connection with the FIXED signature.
        Assert.True(server.Connections.ConnectionsBySignature.TryGetValue(0xCAFEBABEDEADBEEF, out Connections.SynapseConnection? foundSynapseConnection));
        Assert.NotNull(foundSynapseConnection);
        Assert.True(provider.Calls > 0);
    }
}
