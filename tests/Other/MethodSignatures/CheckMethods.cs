﻿using NUnit.Framework;

using Mighty.Interfaces;
using System.Data.Common;
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework.Constraints;

namespace Mighty.MethodSignatures
{
    [TestFixture]
    public class CheckMethods
    {
        private readonly MethodChecker<MightyOrmAbstractInterface<TestGeneric>, TestGeneric> interfaceDefinedMethods;
        private readonly MethodChecker<MightyOrm, dynamic> dynamicDefinedMethods;
        private readonly MethodChecker<MightyOrm<TestGeneric>, TestGeneric> genericDefinedMethods;

        public class TestGeneric { }

        /// <summary>
        /// This initialisation stage already does quite a lot of sanity checking as to whether the methods on
        /// each class/interface are as expected.
        /// </summary>
        public CheckMethods()
        {
            // we are also using CheckMethods here as just a placeholder type
            interfaceDefinedMethods = new MethodChecker<MightyOrmAbstractInterface<TestGeneric>, TestGeneric>(true, true);
            dynamicDefinedMethods = new MethodChecker<MightyOrm, dynamic>(false, false);
            genericDefinedMethods = new MethodChecker<MightyOrm<TestGeneric>, TestGeneric>(false, true);
        }

        /// <summary>
        /// In terms of static methods, we are expecting the factory method inherited from Massive and nothing else.
        /// </summary>
        [Test]
        public void StaticFactoryMethods_Present()
        {
            Assert.AreEqual(0, interfaceDefinedMethods[MightySyncType.Static].MethodCount);
            Assert.AreEqual(1, dynamicDefinedMethods[MightySyncType.Static].MethodCount);
            Assert.AreEqual(1, genericDefinedMethods[MightySyncType.Static].MethodCount);
        }

        /// <summary>
        /// We are not expecting any additional methods to be defined on <see cref="MightyOrm"/> (for dynamic type) itself,
        /// they should all be defined in what it derives from, i.e. <see cref="MightyOrm{T}"/> with a T of dynamic.
        /// </summary>
        [Test]
        public void MightyOrm_IsJustMightyOrmDynamic()
        {
            Assert.AreEqual(0, dynamicDefinedMethods[MightySyncType.SyncOnly].MethodCount);
            Assert.AreEqual(0, dynamicDefinedMethods[MightySyncType.Sync].MethodCount);
            Assert.AreEqual(0, dynamicDefinedMethods[MightySyncType.Async].MethodCount);
        }

        /// <summary>
        /// We don't expect the generic class to have any public methods that are not on the abstract interface.
        /// This test only checks the total counts, but assuming that the class actually implements the abstract interface,
        /// that is all we need to check (we must have as many methods, we just need to check that we don't have more).
        /// </summary>
        [Test]
        public void GenericClass_NoExtraMethods()
        {
            Assert.AreEqual(
                interfaceDefinedMethods[MightySyncType.SyncOnly].MethodCount,
                genericDefinedMethods[MightySyncType.SyncOnly].MethodCount);
            Assert.AreEqual(
                interfaceDefinedMethods[MightySyncType.Sync].MethodCount,
                genericDefinedMethods[MightySyncType.Sync].MethodCount);
            Assert.AreEqual(
                interfaceDefinedMethods[MightySyncType.Async].MethodCount,
                genericDefinedMethods[MightySyncType.Async].MethodCount);
        }


        /// <summary>
        /// Confirm that all the hand coded method types were actually found
        /// </summary>
        [Test]
        public void AllMethodTypes_ArePresent()
        {
            foreach (MightyMethodType type in Enum.GetValues(typeof(MightyMethodType)))
            {
                // illegal methods obviously not supposed to be present; factory method not present on interface (and checked for in tests above)
                Assert.That(
                    interfaceDefinedMethods[type].MethodCount,
                    (type == MightyMethodType._Illegal || type == MightyMethodType.Factory) ?
                        (Constraint)Is.EqualTo(0) :
                        (Constraint)Is.GreaterThan(0)
                );
            }
        }

        /// <summary>
        /// As in the case of the caching tests, it's a bit of extra effort to keep the numbers in this test
        /// up to date but it's probably worth it as a sanity check that any changes required here correspond
        /// only to intended changes elsewhere.
        /// </summary>
        /// <remarks>
        /// Given the comments on the tests above, in all other methods below it makes sense to treat
        /// the abstract interface methods as the canonical set of methods for all remaining tests.
        /// </remarks>
        [Test]
        public void Interface_MethodCounts()
        {
            Assert.AreEqual(10, interfaceDefinedMethods[MightySyncType.SyncOnly].MethodCount);
            Assert.AreEqual(
#if KEY_VALUES
                72,
#else
                71,
#endif
                interfaceDefinedMethods[MightySyncType.Sync].MethodCount);
            Assert.AreEqual(
#if NET40
                0,
#else
#if KEY_VALUES
                140,
#else
                138,
#endif
#endif
                interfaceDefinedMethods[MightySyncType.Async].MethodCount);
        }

        private const string CreateCommand = "CreateCommand";
        private const string OpenConnection = "OpenConnection";

        private static readonly Type dbConnectionType = typeof(DbConnection);
        private static readonly Type cancellationTokenType = typeof(CancellationToken);

        /// <summary>
        /// We have three CreateCommand variants.
        /// </summary>
        [Test]
        public void SyncOnlyMethods_CreateCommandCount()
        {
            Assert.AreEqual(3,
                interfaceDefinedMethods[MightySyncType.SyncOnly]
                    .Where(m => m.Name.StartsWith(CreateCommand)).Select(m => m).Count());
        }

        /// <remarks>
        /// Sync only methods do not have a <see cref="DbConnection"/> param, except for two of
        /// the three variants of <see cref="MightyOrm{T}.CreateCommand(string, DbConnection)"/>
        /// (as just checked above), which do.
        /// Because all other sync methods which have a <see cref="DbConnection"/> also have an async variant
        /// (and so are not sync ONLY, as it is meant here).
        /// </remarks>
        [Test]
        public void SyncOnlyMethods_DoNotContainDbConnection()
        {
            interfaceDefinedMethods[MightySyncType.SyncOnly]
                .Where(m => !m.Name.StartsWith(CreateCommand))
                .DoNotContainParamType(dbConnectionType);
        }

        /// <summary>
        /// We expect just one OpenConnection method.
        /// TO DO: Do we want `StartsWith` here, and in the test above?
        /// </summary>
        [Test]
        public void SyncMethods_OpenConnectionCount()
        {
            Assert.AreEqual(1,
                interfaceDefinedMethods[MightySyncType.Sync]
                    .Where(m => m.Name.StartsWith(OpenConnection)).Count());
        }

        /// <summary>
        /// Sync-only methods must not contain a <see cref="CancellationToken"/>
        /// </summary>
        [Test]
        public void SyncOnlyMethods_DoNotContainCancellationToken()
        {
            interfaceDefinedMethods[MightySyncType.SyncOnly]
                .DoNotContainParamType(cancellationTokenType);
        }

        /// <summary>
        /// Sync methods must not contain a <see cref="CancellationToken"/>
        /// </summary>
        [Test]
        public void SyncMethods_DoNotContainCancellationToken()
        {
            interfaceDefinedMethods[MightySyncType.Sync]
                .DoNotContainParamType(cancellationTokenType);
        }

        /// <summary>
        /// For all methods which have one or other of <see cref="DbConnection"/> and <see cref="CancellationToken"/>,
        /// these should always (with one intentional exception!) occur in a consistent order within the parameters.
        /// </summary>
        /// <remarks>
        /// As now implemented (Mighty v4+):
        ///  - <see cref="CancellationToken"/> if present is always the first parameter
        ///  - <see cref="DbConnection"/> if present is always the last parameter before any `params` arguments
        ///         (with one exception for one variant of Single/SingleAsync, where putting it elsewhere
        ///         makes it possible to disambiguate another argment)
        /// </remarks>
        [Test]
        public void DbConnectionAndCancellationToken_OccurInTheRightPlace()
        {
            var objArray = typeof(object[]);
            var variantMethods = interfaceDefinedMethods[mi => mi.variantType != 0];
            foreach (var method in variantMethods)
            {
                int posDbConnection = -1;
                int posCancellationToken = -1;
                bool hasParamsArguments = false;
                var theParams = method.GetParameters();
                int lastParam = theParams.Length - 1;
                for (int i = 0; i < theParams.Length; i++)
                {
                    var param = theParams[i];
                    if (i == lastParam &&
#if NET40
                        param.ParameterType == objArray
#else
                        param.GetCustomAttribute(typeof(ParamArrayAttribute)) != null
#endif
                    )
                    {
                        hasParamsArguments = true;
                    }
                    else if (param.ParameterType == dbConnectionType)
                    {
                        posDbConnection = i;
                    }
                    else if (param.ParameterType == cancellationTokenType)
                    {
                        posCancellationToken = i;
                    }

                    Assert.That(hasParamsArguments, Is.EqualTo(param.ParameterType == objArray), "If this fails the current NET40 'params' argument identification code probably will not work");
                }

                if (posCancellationToken != -1)
                {
                    Assert.That(posCancellationToken, Is.EqualTo(0), $"position of {nameof(CancellationToken)} parameter in {method}");
                }

                if (posDbConnection != -1)
                {
                    var expectedPos = hasParamsArguments ? lastParam - 1 : lastParam;

                    // The one exception in the position of DbConnection, as documented in the comments on the methods themselves
                    if ((method.Name == "Single" || method.Name == "SingleAsync") &&
                        theParams[posCancellationToken + 1].Name == "where" &&
                        theParams[posCancellationToken + 3].Name == "orderBy")
                    {
                        expectedPos = posCancellationToken + 2;
                    }

                    Assert.That(posDbConnection, Is.EqualTo(expectedPos), $"position of {nameof(DbConnection)} parameter in {method}");
                }
            }
        }

        /// <summary>
        /// All sync methods must have an async variant without a <see cref="CancellationToken"/>
        /// </summary>
        /// <remarks>
        /// We've already checked the method return types when gathering the lists, so here we only need to check
        /// that the parameters correspond.
        /// </remarks>
        [Test]
        public void SyncAndAsyncMethods_Correspond()
        {
            var syncMethods = interfaceDefinedMethods[MightySyncType.Sync];
            var asyncMethodsWithoutToken = interfaceDefinedMethods[MightySyncType.Async][mi => (mi.variantType & MightyVariantType.CancellationToken) == 0];
            Assert.Fail();
        }

        /// <summary>
        /// All async methods must have a <see cref="CancellationToken"/> and non-<see cref="CancellationToken"/> variant.
        /// </summary>
        [Test]
        public void AsyncMethods_HaveCancellationTokenAndNonCancellationTokenVariants()
        {
            var asyncMethodsWithoutToken = interfaceDefinedMethods[MightySyncType.Async][mi => (mi.variantType & MightyVariantType.CancellationToken) == 0];
            var asyncMethodsWithToken = interfaceDefinedMethods[MightySyncType.Async][mi => (mi.variantType & MightyVariantType.CancellationToken) == MightyVariantType.CancellationToken];
            Assert.Fail();
        }

        /// <summary>
        /// All sync methods must have a <see cref="DbConnection"/> and non-<see cref="DbConnection"/> variant.
        /// </summary>
        [Test]
        public void SyncMethods_HaveDbConnectionAndNonDbConnectionVariants()
        {
            // TO DO:
            // okay, we need to confirm that DbConnection is in a standard place, and that
            // at least the ones without connection have a with connection variant
            // (not all the ones with connection have a without connection variant, as basically
            // when there are enough optional params already, it doesn't make anything simpler for
            // anyone to have a without connection variant)
            var syncMethodsWithoutDbConnection = interfaceDefinedMethods[MightySyncType.Sync][MightyVariantType.None];
            var syncMethodsWithDbConnection = interfaceDefinedMethods[MightySyncType.Sync][MightyVariantType.DbConnection];
            Assert.Fail();
        }
    }
}
