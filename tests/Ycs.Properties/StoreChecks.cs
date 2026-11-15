using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Properties;

/// <summary>Every transaction of this suite ends with a recount of the struct lists (starts and run-head index).</summary>
[TestClass]
public static class StoreChecks
{
    [AssemblyInitialize]
    public static void RecountAfterEveryTransaction(TestContext context) => CheckAfterTransactions(null) = true;

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "CheckAfterTransactionsForTests")]
    private static extern ref bool CheckAfterTransactions([UnsafeAccessorType("Ycs.StructStore, Ycs")] object store);
}
