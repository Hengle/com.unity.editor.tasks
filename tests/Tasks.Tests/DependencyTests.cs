﻿using System.Threading.Tasks;
using NUnit.Framework;

namespace ThreadingTests
{
	// Unity does not support async/await tests, but it does
	// have a special type of test with a [CustomUnityTest] attribute
	// which mimicks a coroutine in EditMode. This attribute is
	// defined here so the tests can be compiled without
	// referencing Unity, and nunit on the command line
	// outside of Unity can execute the tests. Basically I don't
	// want to keep two copies of all the tests so I run the
	// UnityTest from here
	partial class DependencyTests
	{
		[Test]
		public void RunningDifferentTasksDependingOnSuccessOrFailure_()
		{
			RunTest(RunningDifferentTasksDependingOnSuccessOrFailure);
		}

		[Test]
		public void TaskOnFailureGetsCalledWhenExceptionHappensUpTheChain_()
		{
			RunTest(TaskOnFailureGetsCalledWhenExceptionHappensUpTheChain);
		}

	}
}