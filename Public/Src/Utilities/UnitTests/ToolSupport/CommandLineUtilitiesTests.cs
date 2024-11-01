// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
using BuildXL.ToolSupport;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Core;
using Test.BuildXL.TestUtilities.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace Test.BuildXL.ToolSupport
{
    public sealed class CommandLineUtilitiesTests : XunitBuildXLTest
    {
        public CommandLineUtilitiesTests(ITestOutputHelper output)
            : base(output) { }

        private static void CommonCommandLineTests(CommandLineUtilities clu)
        {
            CommandLineUtilities.Option[] opts = clu.Options.ToArray();
            string[] argStrings = clu.Arguments.ToArray();

            Assert.Equal(4, opts.Length);
            Assert.Equal(3, argStrings.Length);

            Assert.Equal("opt0", opts[0].Name);
            Assert.True(string.IsNullOrEmpty(opts[0].Value));

            Assert.Equal("opt1", opts[1].Name);
            Assert.True(string.IsNullOrEmpty(opts[1].Value));

            Assert.Equal("opt2", opts[2].Name);
            Assert.Equal("abc", opts[2].Value);

            Assert.Equal("opt3", opts[3].Name);
            Assert.Equal("abc xyz", opts[3].Value);

            Assert.Equal("Arg0", argStrings[0]);
            Assert.Equal("Arg1", argStrings[1]);
            Assert.Equal("Arg2a Arg2b", argStrings[2]);
        }

        [Fact]
        public void CommandLineUtilsTest()
        {
            var args = new string[]
                       {
                           "/opt0",
                           "/opt1:",
                           "/opt2:abc",
                           "/opt3:abc xyz",
                           "Arg0",
                           "Arg1",
                           "Arg2a Arg2b",
                       };

            var clu = new CommandLineUtilities(args);
            CommonCommandLineTests(clu);
        }

        [Fact(Skip = "Quote escaping in response files is no longer supported")]
        public void ResponseFileCombo()
        {
            string tempPath = Path.Combine(TestOutputDirectory, "ResponseFileCombo");

            File.WriteAllText(
                tempPath,
                @"/opt0
/opt1:
/opt2:abc
""/opt3:abc xyz""
Arg0
""""

Arg1
""Arg2a Arg2b""");

            var clu = new CommandLineUtilities(new string[] { "@" + tempPath });
            CommonCommandLineTests(clu);

            try
            {
                // cleanup
                File.Delete(tempPath);
            }
#pragma warning disable ERP022 // Unobserved exception in generic exception handler
            catch
            {
                // don't care...
            }
#pragma warning restore ERP022 // Unobserved exception in generic exception handler
        }

        [Fact(Skip = "Quote escaping in response files is no longer supported")]
        public void ResponseFiles()
        {
            string tempPath = Path.Combine(TestOutputDirectory, "ResponseFiles");

            File.WriteAllText(
                tempPath,
                @"/opt0
/opt1:
/opt2:abc
""/opt3:abc xyz""
Arg0
Arg1");

            var clu = new CommandLineUtilities(new[] { "@" + tempPath, "Arg2a Arg2b" });
            CommonCommandLineTests(clu);

            try
            {
                // cleanup
                File.Delete(tempPath);
            }
#pragma warning disable ERP022 // Unobserved exception in generic exception handler
            catch
            {
                // don't care...
            }
#pragma warning restore ERP022 // Unobserved exception in generic exception handler
        }

        [Fact]

        public void BadResponseFile()
        {
            string tempPath = Path.Combine(TestOutputDirectory, "BadResponseFile");
            Assert.Throws<InvalidArgumentException>(() => Analysis.IgnoreResult(new CommandLineUtilities(new[] { "@\"" + tempPath + "\"" })));
        }

        [Fact]
        public void BooleanOption()
        {
            CommandLineUtilities.Option opt = default(CommandLineUtilities.Option);

            opt.Name = "Switch";
            opt.Value = null;
            Assert.True(CommandLineUtilities.ParseBooleanOption(opt));

            opt.Name = "Switch";
            opt.Value = string.Empty;
            Assert.True(CommandLineUtilities.ParseBooleanOption(opt));

            opt.Name = "Switch+";
            opt.Value = null;
            Assert.True(CommandLineUtilities.ParseBooleanOption(opt));

            opt.Name = "Switch+";
            opt.Value = string.Empty;
            Assert.True(CommandLineUtilities.ParseBooleanOption(opt));

            opt.Name = "Switch-";
            opt.Value = null;
            Assert.False(CommandLineUtilities.ParseBooleanOption(opt));

            opt.Name = "Switch-";
            opt.Value = string.Empty;
            Assert.False(CommandLineUtilities.ParseBooleanOption(opt));

            Assert.Throws<InvalidArgumentException>(() =>
                {
                    opt.Name = "Switch+";
                    opt.Value = "X";
                    CommandLineUtilities.ParseBooleanOption(opt);
                });
        }

        [Fact]
        public void VoidOption()
        {
            CommandLineUtilities.Option opt = default(CommandLineUtilities.Option);

            opt.Name = "Switch";
            opt.Value = null;
            CommandLineUtilities.ParseVoidOption(opt);

            opt.Name = "Switch";
            opt.Value = string.Empty;
            CommandLineUtilities.ParseVoidOption(opt);

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = "X";
                CommandLineUtilities.ParseVoidOption(opt);
            });
        }

        [Fact]
        public void StringOption()
        {
            CommandLineUtilities.Option opt = default(CommandLineUtilities.Option);

            opt.Name = "Switch";
            opt.Value = "Value";
            Assert.Equal(opt.Value, CommandLineUtilities.ParseStringOption(opt));

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = null;
                CommandLineUtilities.ParseStringOption(opt);
            });

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = string.Empty;
                CommandLineUtilities.ParseStringOption(opt);
            });
        }

        [Fact]
        public void SingletonStringOption()
        {
            CommandLineUtilities.Option opt = default(CommandLineUtilities.Option);

            opt.Name = "Switch";
            opt.Value = "Value";
            Assert.Equal(opt.Value, CommandLineUtilities.ParseSingletonStringOption(opt, null));

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = null;
                CommandLineUtilities.ParseSingletonStringOption(opt, null);
            });

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = string.Empty;
                CommandLineUtilities.ParseSingletonStringOption(opt, null);
            });

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = "New";
                CommandLineUtilities.ParseSingletonStringOption(opt, "Existing");
            });
        }

        [Fact]
        public void PathOption()
        {
            CommandLineUtilities.Option opt = default(CommandLineUtilities.Option);

            opt.Name = "Switch";
            opt.Value = "test.dll";
            Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), "test.dll"), CommandLineUtilities.ParsePathOption(opt));

            // Paths may be wrapped in quotes
            opt.Value = "\"File With Spaces.dll\"";
            Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), "File With Spaces.dll"), CommandLineUtilities.ParsePathOption(opt));

            if (!OperatingSystemHelper.IsUnixOS)
            {
                opt.Name = "Switch";
                opt.Value = SpecialFolderUtilities.GetFolderPath(Environment.SpecialFolder.Windows);
                Assert.Equal(opt.Value, CommandLineUtilities.ParsePathOption(opt));
            }

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = null;
                CommandLineUtilities.ParsePathOption(opt);
            });

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = string.Empty;
                CommandLineUtilities.ParsePathOption(opt);
            });
        }

        [Fact]
        public void SingletonPathOption()
        {
            CommandLineUtilities.Option opt = default(CommandLineUtilities.Option);

            opt.Name = "Switch";
            opt.Value = "test.dll";
            Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), "test.dll"), CommandLineUtilities.ParseSingletonPathOption(opt, null));

            if(!OperatingSystemHelper.IsUnixOS)
            {
                opt.Name = "Switch";
                opt.Value = SpecialFolderUtilities.GetFolderPath(Environment.SpecialFolder.Windows);
                Assert.Equal(opt.Value, CommandLineUtilities.ParseSingletonPathOption(opt, null));
            }

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = null;
                CommandLineUtilities.ParseSingletonPathOption(opt, null);
            });

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = string.Empty;
                CommandLineUtilities.ParseSingletonPathOption(opt, null);
            });

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = "New";
                CommandLineUtilities.ParseSingletonPathOption(opt, "Existing");
            });
        }

        [Fact]
        public void RepeatingPathOption()
        {
            CommandLineUtilities.Option opt = default(CommandLineUtilities.Option);

            opt.Name = "Switch";
            opt.Value = "test.dll";
            Assert.True(
                CommandLineUtilities.ParseRepeatingPathOption(opt, ",").SequenceEqual(
                    new string[]
                    {
                        Path.Combine(Directory.GetCurrentDirectory(), "test.dll")
                    }));

            opt.Name = "Switch";
            opt.Value = "test.dll,test2.dll";
            Assert.True(
                CommandLineUtilities.ParseRepeatingPathOption(opt, ",").SequenceEqual(
                    new string[]
                    {
                        Path.Combine(Directory.GetCurrentDirectory(), "test.dll"),
                        Path.Combine(Directory.GetCurrentDirectory(), "test2.dll")
                    }));

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = null;
                CommandLineUtilities.ParseStringOption(opt);
            });
        }

        [Fact]
        public void Int32Option()
        {
            CommandLineUtilities.Option opt = default(CommandLineUtilities.Option);

            opt.Name = "Switch";
            opt.Value = "12";
            Assert.Equal(12, CommandLineUtilities.ParseInt32Option(opt, 0, 100));

            opt.Name = "Switch";
            opt.Value = "0";
            Assert.Equal(0, CommandLineUtilities.ParseInt32Option(opt, 0, 100));

            opt.Name = "Switch";
            opt.Value = "100";
            Assert.Equal(100, CommandLineUtilities.ParseInt32Option(opt, 0, 100));

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = null;
                CommandLineUtilities.ParseInt32Option(opt, 0, 100);
            });

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = string.Empty;
                CommandLineUtilities.ParseInt32Option(opt, 0, 100);
            });

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = "-1";
                CommandLineUtilities.ParseInt32Option(opt, 0, 100);
            });

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = "101";
                CommandLineUtilities.ParseInt32Option(opt, 0, 100);
            });
        }

        [Fact]
        public void Int64Option()
        {
            CommandLineUtilities.Option opt = default(CommandLineUtilities.Option);

            opt.Name = "Switch";
            opt.Value = "12";
            Assert.Equal(12, CommandLineUtilities.ParseInt64Option(opt, 0, 100));

            opt.Name = "Switch";
            opt.Value = "0";
            Assert.Equal(0, CommandLineUtilities.ParseInt64Option(opt, 0, 100));

            opt.Name = "Switch";
            opt.Value = "100";
            Assert.Equal(100, CommandLineUtilities.ParseInt64Option(opt, 0, 100));

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = null;
                CommandLineUtilities.ParseInt64Option(opt, 0, 100);
            });

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = string.Empty;
                CommandLineUtilities.ParseInt64Option(opt, 0, 100);
            });

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = "-1";
                CommandLineUtilities.ParseInt64Option(opt, 0, 100);
            });

            Assert.Throws<InvalidArgumentException>(() =>
            {
                opt.Name = "Switch";
                opt.Value = "101";
                CommandLineUtilities.ParseInt64Option(opt, 0, 100);
            });
        }

        [Fact]
        public void CommandLineUtilsMarkerTest()
        {
            var callerArgs = new string[]
                       {
                           "/opt0",
                           "/opt1:abc",
                           "Arg0",
                           "Arg1",
                       };

            var forwardingArgs = new string[]
                        {
                           "Arg2",
                           "/opt2",
                           "Arg3",
                        };


            var args = callerArgs.Append("--").Union(forwardingArgs).ToReadOnlyArray();
                       
            var clu = new CommandLineUtilities(args);

            // Validate expanded args
            XAssert.ArrayEqual(args.ToArray(), clu.GetExpandedArguments(ArgumentOption.All).ToArray());
            XAssert.ArrayEqual(callerArgs.ToArray(), clu.GetExpandedArguments(ArgumentOption.CallerArguments).ToArray());
            XAssert.ArrayEqual(forwardingArgs.ToArray(), clu.GetExpandedArguments(ArgumentOption.ForwardingArguments).ToArray());

            // Validate unadorned args
            XAssert.ArrayEqual(new[] { "Arg0", "Arg1", "--", "Arg2", "Arg3" }, clu.GetArguments(ArgumentOption.All).ToArray());
            XAssert.ArrayEqual(new[] { "Arg0", "Arg1" }, clu.GetArguments(ArgumentOption.CallerArguments).ToArray());
            XAssert.ArrayEqual(new[] { "Arg2", "Arg3" }, clu.GetArguments(ArgumentOption.ForwardingArguments).ToArray());

            // Validate options
            XAssert.ArrayEqual(new[] { "/opt0", "/opt1:abc", "/opt2" }, clu.GetOptions(ArgumentOption.All).Select(option => option.PrintCommandLineString()).ToArray());
            XAssert.ArrayEqual(new[] { "/opt0", "/opt1:abc" }, clu.GetOptions(ArgumentOption.CallerArguments).Select(option => option.PrintCommandLineString()).ToArray());
            XAssert.ArrayEqual(new[] { "/opt2" }, clu.GetOptions(ArgumentOption.ForwardingArguments).Select(option => option.PrintCommandLineString()).ToArray());
        }

        [Fact]
        public void CommandLineUtilsMarkerAbsentTest()
        {
            // No end of caller mark
            var args = new string[]
                       {
                           "/opt0",
                           "/opt1:abc",
                           "Arg0",
                           "Arg1",
                       };

            var clu = new CommandLineUtilities(args);

            // Validate expanded args
            XAssert.ArrayEqual(args, clu.GetExpandedArguments(ArgumentOption.All).ToArray());
            XAssert.ArrayEqual(args, clu.GetExpandedArguments(ArgumentOption.CallerArguments).ToArray());
            XAssert.ArrayEqual(CollectionUtilities.EmptyArray<string>(), clu.GetExpandedArguments(ArgumentOption.ForwardingArguments).ToArray());

            // Validate unadorned args
            XAssert.ArrayEqual(new[] { "Arg0", "Arg1"}, clu.GetArguments(ArgumentOption.All).ToArray());
            XAssert.ArrayEqual(new[] { "Arg0", "Arg1" }, clu.GetArguments(ArgumentOption.CallerArguments).ToArray());
            XAssert.ArrayEqual(CollectionUtilities.EmptyArray<string>(), clu.GetArguments(ArgumentOption.ForwardingArguments).ToArray());

            // Validate options
            XAssert.ArrayEqual(new[] { "/opt0", "/opt1:abc" }, clu.GetOptions(ArgumentOption.All).Select(option => option.PrintCommandLineString()).ToArray());
            XAssert.ArrayEqual(new[] { "/opt0", "/opt1:abc" }, clu.GetOptions(ArgumentOption.CallerArguments).Select(option => option.PrintCommandLineString()).ToArray());
            XAssert.ArrayEqual(CollectionUtilities.EmptyArray<string>(), clu.GetOptions(ArgumentOption.ForwardingArguments).Select(option => option.PrintCommandLineString()).ToArray());
        }

        [Theory]
        [InlineData(new[] { "/opt0", "Arg0", "--", "/opt1", "Arg1"}, true)]
        [InlineData(new[] { "/opt0", "Arg0", "/opt1", "Arg1" }, false)]
        [InlineData(new[] { "--" }, true)]
        [InlineData(new string[] { }, false)]
        public void HasEndOfCallerArgumentTest(string[] args, bool expectedEndOfCallerArgument)
        {
            var clu = new CommandLineUtilities(args);
            XAssert.AreEqual(expectedEndOfCallerArgument, clu.HasEndOfCallerArgument);
        }

        [Theory]
        [InlineData("100", 100)]
        [InlineData("20000ms", 20_000)]
        [InlineData("100s", 100_000)]
        [InlineData("1.5s", 1500)]
        [InlineData("1m", 60_000)]
        [InlineData("60m", 3_600_000)]
        [InlineData("60min", 3_600_000)]
        [InlineData("0.5h", 1_800_000)]
        [InlineData("1h", 3_600_000)]
        [InlineData("999999999999999h", -1)]
        [InlineData("233a", -1)]
        [InlineData("233mm", -1)]
        [InlineData("233msh", -1)]
        [InlineData("", -1)]
        [InlineData(null, -1)]
        public void ParseTimeToMsOption(string value, int expectedValue)
        {
            const int MinValue = 0;
            const int MaxValue = int.MaxValue;

            var opt = new CommandLineUtilities.Option
            {
                Name = "time",
                Value = value
            };

            if (expectedValue == -1)
            {
                Assert.Throws<InvalidArgumentException>(() =>
                {
                    CommandLineUtilities.ParseDurationOptionToMilliseconds(opt, MinValue, MaxValue);
                });
            }
            else
            {
                XAssert.AreEqual(expectedValue, CommandLineUtilities.ParseDurationOptionToMilliseconds(opt, MinValue, MaxValue));
            }
        }
    }
}
