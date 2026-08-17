using System.Net;
using SynapseSocket.Connections;
using Xunit;

namespace SynapseSocket.Tests.Lifecycle;

/// <summary>
/// Coverage for the dense connections list keeping every entry's recorded index equal to the slot it occupies.
/// </summary>
/// <remarks>
/// <see cref="ConnectionManager"/> holds connections twice: a dictionary keyed by endpoint, and a dense list the maintenance sweep walks. Each connection records its own slot in that list, and the sweep, the keep-alive, the timeout detection and the reliable retransmit all reach a connection only through the list. A removal that leaves a recorded index pointing at the wrong slot therefore does not corrupt a lookup, it silently drops a live peer out of maintenance on the NEXT removal, which is why the invariant is asserted directly here rather than inferred from behaviour.
/// The existing suites never reach this: every one of them disconnects a client that is the only connection, which is the single case the arithmetic happened to get right.
/// </remarks>
public class ConnectionsListIntegrityTests
{
    /// <summary>
    /// The loopback port connections are given, which is never bound because no socket is opened here.
    /// </summary>
    private const int BasePort = 45_000;

    /// <summary>
    /// Tests that removing from the middle of the list twice leaves every survivor recorded at the slot it actually occupies.
    /// </summary>
    /// <remarks>Two removals rather than one on purpose. A single removal leaves the list CONTENTS correct, so it looks healthy; the damage is to the recorded indices, and it only becomes visible when the next removal reads one of them.</remarks>
    [Fact]
    public void Remove_FromTheMiddleTwice_KeepsEverySurvivorAtItsRecordedIndex()
    {
        ConnectionManager connectionManager = new();

        IPEndPoint first = Endpoint(0);
        IPEndPoint second = Endpoint(1);
        IPEndPoint third = Endpoint(2);
        IPEndPoint fourth = Endpoint(3);

        SynapseConnection firstConnection = connectionManager.CreateNew(first, signature: 1);
        SynapseConnection secondConnection = connectionManager.CreateNew(second, signature: 2);
        SynapseConnection thirdConnection = connectionManager.CreateNew(third, signature: 3);
        SynapseConnection fourthConnection = connectionManager.CreateNew(fourth, signature: 4);

        AssertRecordedIndicesMatchSlots(connectionManager);

        Assert.True(connectionManager.Remove(second, out _));
        AssertRecordedIndicesMatchSlots(connectionManager);

        Assert.True(connectionManager.Remove(third, out _));
        AssertRecordedIndicesMatchSlots(connectionManager);

        // The two connections never removed must both still be reachable through the list the maintenance sweep walks.
        Assert.Contains(firstConnection, connectionManager.Connections);
        Assert.Contains(fourthConnection, connectionManager.Connections);

        // And the two that were removed must not be, or a dead peer is maintained forever.
        Assert.DoesNotContain(secondConnection, connectionManager.Connections);
        Assert.DoesNotContain(thirdConnection, connectionManager.Connections);
    }

    /// <summary>
    /// Tests that removing the tail clears the outgoing connection's recorded index, as removing from the middle does.
    /// </summary>
    /// <remarks>The clear used to sit inside the swap branch, so a tail removal handed back a connection still carrying a live looking index. Nothing resets it afterwards, because a removed connection is not returned to its pool.</remarks>
    [Fact]
    public void Remove_OfTheTail_ClearsTheOutgoingRecordedIndex()
    {
        ConnectionManager connectionManager = new();

        IPEndPoint first = Endpoint(0);
        IPEndPoint second = Endpoint(1);

        connectionManager.CreateNew(first, signature: 1);
        SynapseConnection tailConnection = connectionManager.CreateNew(second, signature: 2);

        Assert.True(connectionManager.Remove(second, out _));

        Assert.Equal(SynapseConnection.UnsetConnectionsIndex, tailConnection.ConnectionsIndex);
    }

    /// <summary>
    /// Tests that replacing an existing endpoint keeps the list consistent, which is the second place the swap-remove was written out.
    /// </summary>
    [Fact]
    public void CreateNew_OverAnExistingMiddleEndpoint_KeepsEverySurvivorAtItsRecordedIndex()
    {
        ConnectionManager connectionManager = new();

        IPEndPoint first = Endpoint(0);
        IPEndPoint second = Endpoint(1);
        IPEndPoint third = Endpoint(2);

        connectionManager.CreateNew(first, signature: 1);
        connectionManager.CreateNew(second, signature: 2);
        SynapseConnection thirdConnection = connectionManager.CreateNew(third, signature: 3);

        // Re-creating over the middle endpoint removes the old entry and appends a new one, through the same arithmetic.
        connectionManager.CreateNew(second, signature: 4);

        AssertRecordedIndicesMatchSlots(connectionManager);
        Assert.Contains(thirdConnection, connectionManager.Connections);
    }

    /// <summary>
    /// Asserts the dense list's defining invariant: the entry at each slot records that slot as its own.
    /// </summary>
    /// <param name="connectionManager">The manager to inspect.</param>
    private static void AssertRecordedIndicesMatchSlots(ConnectionManager connectionManager)
    {
        for (int i = 0; i < connectionManager.Connections.Count; i++)
            Assert.True(connectionManager.Connections[i].ConnectionsIndex == i, $"The connection at slot [{i}] records its index as [{connectionManager.Connections[i].ConnectionsIndex}], so the next removal will act on the wrong slot.");
    }

    /// <summary>
    /// Builds a distinct loopback endpoint, which is never bound because this suite opens no socket.
    /// </summary>
    /// <param name="offset">The offset from the base port.</param>
    /// <returns>The endpoint.</returns>
    private static IPEndPoint Endpoint(int offset) => new(IPAddress.Loopback, BasePort + offset);
}
