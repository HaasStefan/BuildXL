// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.ContractsLight;
using System.IO;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Configuration.Mutable;
using BuildXL.Utilities.Core;

namespace BuildXL.Pips.Operations
{
    /// <summary>
    /// An extended binary writer that can read Pips
    /// </summary>
    /// <remarks>
    /// This type is internal, as the serialization/deserialization functionality is encapsulated by the PipTable.
    /// </remarks>
    internal class PipReader : BuildXLReader
    {
        public PipReader(bool debug, StringTable stringTable, Stream stream, bool leaveOpen)
            : base(debug, stream, leaveOpen)
        {
            Contract.Requires(stringTable != null);
            StringTable = stringTable;
        }

        public StringTable StringTable { get; }

        public virtual Pip ReadPip()
        {
            Start<Pip>();
            Pip value = Pip.Deserialize(this);
            End();
            return value;
        }

        public virtual PipProvenance ReadPipProvenance()
        {
            Start<PipProvenance>();
            PipProvenance value = PipProvenance.Deserialize(this);
            End();
            return value;
        }

        public virtual StringId ReadPipDataEntriesPointer()
        {
            return ReadStringId();
        }

        public virtual PipData ReadPipData()
        {
            Start<PipData>();
            PipData value = PipData.Deserialize(this);
            End();
            return value;
        }

        public virtual EnvironmentVariable ReadEnvironmentVariable()
        {
            Start<EnvironmentVariable>();
            EnvironmentVariable value = EnvironmentVariable.Deserialize(this);
            End();
            return value;
        }

        public virtual RegexDescriptor ReadRegexDescriptor()
        {
            Start<RegexDescriptor>();
            RegexDescriptor value = RegexDescriptor.Deserialize(this);
            End();
            return value;
        }

        public virtual PipId ReadPipId()
        {
            Start<PipId>();
            var value = new PipId(base.ReadUInt32());
            End();
            return value;
        }

        public virtual ProcessSemaphoreInfo ReadProcessSemaphoreInfo()
        {
            Start<ProcessSemaphoreInfo>();
            var value = ProcessSemaphoreInfo.Deserialize(this);
            End();
            return value;
        }

        public virtual PipId RemapPipId(PipId pipId) => pipId;

        public virtual WriteFile.Options ReadWriteFileOptions()
        {
            Start<WriteFile.Options>();
            var value = WriteFile.Options.Deserialize(this);
            End();
            return value;
        }

        public virtual IBreakawayChildProcess ReadBreakawayChildProcess()
        {
            Start<IBreakawayChildProcess>();
            var processName = ReadPathAtom();
            var requiredArgs = ReadString();
            var ignoreCase = ReadBoolean();
            End();
            return new BreakawayChildProcess() { ProcessName = processName, RequiredArguments = requiredArgs, RequiredArgumentsIgnoreCase = ignoreCase };
        }

        /// <summary>
        /// Reads a ReadOnlyArray
        /// </summary>
        public ReadOnlyArray<T> ReadReadOnlyArray<T>(Func<PipReader, T> reader)
        {
            Contract.RequiresNotNull(reader);
            Start<ReadOnlyArray<T>>();
            int length = ReadInt32Compact();
            if (length == 0)
            {
                End();
                return ReadOnlyArray<T>.Empty;
            }

            T[] array = ReadArrayCore(reader, length);

            End();
            return ReadOnlyArray<T>.FromWithoutCopy(array);
        }

        private T[] ReadArrayCore<T>(Func<PipReader, T> reader, int length, int minimumLength = 0)
        {
            var array = CollectionUtilities.NewOrEmptyArray<T>(Math.Max(minimumLength, length));
            for (int i = 0; i < length; i++)
            {
                array[i] = reader(this);
            }

            return array;
        }
    }
}
