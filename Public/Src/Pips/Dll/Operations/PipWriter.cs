// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Core;

namespace BuildXL.Pips.Operations
{
    /// <summary>
    /// An extended binary writer that can write Pips
    /// </summary>
    /// <remarks>
    /// This type is internal, as the serialization/deserialization functionality is encapsulated by the PipTable.
    /// </remarks>
    internal class PipWriter : BuildXLWriter
    {
        public PipWriter(bool debug, Stream stream, bool leaveOpen, bool logStats)
            : base(debug, stream, leaveOpen, logStats)
        {
        }

        public virtual void Write(Pip pip)
        {
            Contract.Requires(pip != null);
            Start<Pip>();
            pip.Serialize(this);
            End();
        }

        public virtual void Write(in PipData value)
        {
            Start<PipData>();
            value.Serialize(this);
            End();
        }

        public virtual void WritePipDataEntriesPointer(in StringId value)
        {
            Write(value);
        }

        public virtual void Write(in EnvironmentVariable value)
        {
            Start<EnvironmentVariable>();
            value.Serialize(this);
            End();
        }

        public virtual void Write(RegexDescriptor value)
        {
            Start<RegexDescriptor>();
            value.Serialize(this);
            End();
        }

        public virtual void Write(PipProvenance value)
        {
            Contract.Requires(value != null);
            Start<PipProvenance>();
            value.Serialize(this);
            End();
        }

        public virtual void Write(PipId value)
        {
            Start<PipId>();
            Write(value.Value);
            End();
        }

        public virtual void Write(in ProcessSemaphoreInfo value)
        {
            Start<ProcessSemaphoreInfo>();
            value.Serialize(this);
            End();
        }

        public virtual void Write(in IBreakawayChildProcess value)
        {
            Start<IBreakawayChildProcess>();
            Write(value.ProcessName);
            Write(value.RequiredArguments);
            Write(value.RequiredArgumentsIgnoreCase);
            End();
        }

        public virtual void Write(in WriteFile.Options value)
        {
            Start<WriteFile.Options>();
            value.Serialize(this);
            End();
        }

        /// <summary>
        /// Writes a ReadOnlyArray
        /// </summary>
        public void Write<T>(ReadOnlyArray<T> value, Action<PipWriter, T> write)
        {
            Contract.Requires(value.IsValid);
            WriteReadOnlyListCore(value, write);
        }

        private void WriteReadOnlyListCore<T, TReadOnlyList>(TReadOnlyList value, Action<PipWriter, T> write)
            where TReadOnlyList : IReadOnlyList<T>
        {
            Start<TReadOnlyList>();
            WriteCompact(value.Count);
            for (int i = 0; i < value.Count; i++)
            {
                write(this, value[i]);
            }

            End();
        }
    }
}
