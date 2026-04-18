using System;
using System.Reflection;
using System.Threading;
using Xunit.Sdk;

[assembly: SynapseSocket.Tests.SettleAfterTest]

namespace SynapseSocket.Tests;

[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SettleAfterTestAttribute : BeforeAfterTestAttribute
{
    private readonly int _beforeMs;
    private readonly int _afterMs;

    public SettleAfterTestAttribute(int afterMs = 300, int beforeMs = 0)
    {
        _afterMs = afterMs;
        _beforeMs = beforeMs;
    }

    public override void Before(MethodInfo methodUnderTest) { if (_beforeMs > 0) Thread.Sleep(_beforeMs); }

    public override void After(MethodInfo methodUnderTest) => Thread.Sleep(_afterMs);
}
