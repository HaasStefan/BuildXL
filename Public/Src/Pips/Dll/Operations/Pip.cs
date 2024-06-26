// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using BuildXL.Ipc.Interfaces;
using BuildXL.Storage.Fingerprints;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;
using static BuildXL.Utilities.Core.FormattableStringEx;

namespace BuildXL.Pips.Operations
{
    /// <summary>
    /// Primitive Indivisible Processing, representing the smallest schedulable unit of work.
    /// </summary>
    /// <remarks>
    /// There are only few subtypes: <code>CopyFile</code>, <code>WriteFile</code>, <code>Process</code>, <code>HashSourceFile</code>, <code>SealDirectory</code>.
    /// TODO: Introduce some intermediate abstract classes, reflecting that only some pips have a provenance, and that only some pips have tags, and that only some pips have descriptions.
    /// A <code>Pip</code> is strictly immutable (except for its <code>PipId</code> which is set once). All mutable information is held by <code>MutablePipState</code>.
    /// </remarks>
    public abstract class Pip
    {
        /// <summary>
        /// The prefix to use when reporting the semistable hash.
        /// </summary>
        public const string SemiStableHashPrefix = "Pip";

        /// <summary>
        /// Regex that matches a formatted semistable hash
        /// </summary>
        private static readonly Regex s_formattedSemiStableHashRegex = new(@$"{SemiStableHashPrefix}[a-fA-F0-9]{{16,}}");

        private PipId m_pipId;

        /// <summary>
        /// Independent static fingerprint of a pip that does not include the static fingerprints of its dependencies.
        /// </summary>
        /// <remarks>
        /// Independent static fingerprint is fingerprint that is computed without including the fingerprints of pip's dependencies.
        /// To support graph-agnostic incremental scheduling, the process pip's static fingerprint must include the static fingerprints of its seal directory dependencies
        /// otherwise changes in the members of the seal directory may not invalidate the consuming process pip.
        /// However, computing such static fingerprints is not always possible in the case of binary graph fragment. A process pip in a fragment can be dependent on
        /// a seal directory declared in another fragment. Thus, when the static fingerprint of the process pip is computed, the static fingerprint of the seal directory
        /// does not exist. In such a case, we compute an independent static fingerprint for the process pip. Then, upon merging the fragments into a full pip graph,
        /// we compute the static fingerprint that includes the seal directory's fingerprint.
        /// </remarks>
        public Fingerprint IndependentStaticFingerprint { get; set; }

        /// <summary>
        /// Tags used to enable pip-level filtering of the schedule.
        /// </summary>
        [PipCaching(FingerprintingRole = FingerprintingRole.None)]
        public abstract ReadOnlyArray<StringId> Tags { get; }

        /// <summary>
        /// Pip provenance.
        /// </summary>
        [PipCaching(FingerprintingRole = FingerprintingRole.None)]
        public abstract PipProvenance Provenance { get; }

        /// <summary>
        /// Exposes the type of the pip.
        /// </summary>
        [PipCaching(FingerprintingRole = FingerprintingRole.None)]
        public abstract PipType PipType { get; }

        /// <summary>
        /// When set, the pip fingerprint is not sensitive to fingerprint salts. This excludes both EngineEnvironmentSettings.DebugFingerprintSalt and PipFingerprintingVersion.TwoPhaseV2
        /// </summary>
        public virtual bool BypassFingerprintSalt => false;

        /// <nodoc />
        internal Pip()
        {
        }

        /// <summary>
        /// Unique Pip Id assigned when Pip is added to table
        /// </summary>
        /// <remarks>
        /// This property can be set only once.
        /// </remarks>
        [PipCaching(FingerprintingRole = FingerprintingRole.None)]
        public PipId PipId
        {
            get
            {
                return m_pipId;
            }

            internal set
            {
                Contract.Requires(value.IsValid);
                Contract.Requires(!PipId.IsValid);
                m_pipId = value;
            }
        }

        /// <summary>
        /// Identifier of this pip that is stable across BuildXL runs with an identical schedule
        /// </summary>
        /// <remarks>
        /// This identifier is not necessarily unique, but should be quite unique in practice.
        /// </remarks>
        [PipCaching(FingerprintingRole = FingerprintingRole.None)]
        public long SemiStableHash => Provenance?.SemiStableHash ?? 0;

        /// <summary>
        /// A SemiStableHash formatted as a string for display
        /// </summary>
        public string FormattedSemiStableHash => FormatSemiStableHash(SemiStableHash);

        /// <summary>
        /// Format the semistable hash for display 
        /// </summary>
        /// <remarks>
        /// Keep in sync with <see cref="s_formattedSemiStableHashRegex"/> and <see cref="TryParseSemiStableHash(string, out long)"/>
        /// CODESYNC: Make sure to update 'GetStdInFilePath' in 'SandboxedProcessUnix.cs' when this logic changes!!!
        /// </remarks>
        public static string FormatSemiStableHash(long hash) => PipSemitableHash.Format(hash, includePrefix: true);

        /// <summary>
        /// Inverse of <see cref="FormatSemiStableHash"/>
        /// </summary>
        public static bool TryParseSemiStableHash(string formattedSemiStableHash, out long hash)
        {
            return PipSemitableHash.TryParseSemiStableHash(formattedSemiStableHash, out hash);
        }

        /// <summary>
        /// Extract all appearances of semistable hashes in a text. The hashes are assumed to be formatted by <see cref="FormatSemiStableHash"/>
        /// </summary>
        public static IReadOnlyList<long> ExtractSemistableHashes(string text)
        {
            Contract.Requires(text != null);

            var matches = s_formattedSemiStableHashRegex.Matches(text);

            var result = new List<long>(matches.Count);
            for (var i = 0; i < matches.Count; i++)
            {
                var capture = matches[i].Captures[0].Value;
                if(TryParseSemiStableHash(capture, out var hash))
                {
                    result.Add(hash);
                }
            }

            return result;
        }

        /// <summary>
        /// Whether this is a process pip that <see cref="Process.AllowUndeclaredSourceReads"/>
        /// </summary>
        public bool ProcessAllowsUndeclaredSourceReads => this is Process process && process.AllowUndeclaredSourceReads;
        
        /// <summary>
        /// Resets pip id.
        /// </summary>
        /// <remarks>
        /// This method should only be used for graph patching and in unit tests.
        /// </remarks>
        public void ResetPipId()
        {
            m_pipId = PipId.Invalid;
        }

        #region Serialization
        internal static Pip Deserialize(PipReader reader)
        {
            Contract.Requires(reader != null);
            var b = reader.ReadByte();
            Contract.Assert(b <= (int)PipType.SealDirectory);
            var pipType = (PipType)b;
            Pip pip;
            switch (pipType)
            {
                case PipType.CopyFile:
                    reader.Start<CopyFile>();
                    pip = CopyFile.InternalDeserialize(reader);
                    break;
                case PipType.HashSourceFile:
                    reader.Start<HashSourceFile>();
                    pip = HashSourceFile.InternalDeserialize(reader);
                    break;
                case PipType.Process:
                    reader.Start<Process>();
                    pip = Process.InternalDeserialize(reader);
                    break;
                case PipType.Ipc:
                    reader.Start<IpcPip>();
                    pip = IpcPip.InternalDeserialize(reader);
                    break;
                case PipType.SealDirectory:
                    reader.Start<SealDirectory>();
                    pip = SealDirectory.InternalDeserialize(reader);
                    break;
                case PipType.Value:
                    reader.Start<ValuePip>();
                    pip = ValuePip.InternalDeserialize(reader);
                    break;
                case PipType.SpecFile:
                    reader.Start<SpecFilePip>();
                    pip = SpecFilePip.InternalDeserialize(reader);
                    break;
                case PipType.Module:
                    reader.Start<ModulePip>();
                    pip = ModulePip.InternalDeserialize(reader);
                    break;
                default:
                    Contract.Assert(pipType == PipType.WriteFile);
                    reader.Start<WriteFile>();
                    pip = WriteFile.InternalDeserialize(reader);
                    break;
            }

            if (reader.ReadBoolean())
            {
                pip.IndependentStaticFingerprint = FingerprintUtilities.CreateFrom(reader);
            }
            reader.End();
            Contract.Assume(pip != null);
            return pip;
        }

        internal void Serialize(PipWriter writer)
        {
            Contract.Requires(writer != null);
            writer.Write((byte)PipType);
            writer.Start(GetType());
            InternalSerialize(writer);
            if (IndependentStaticFingerprint.Length >0)
            {
                writer.Write(true);
                IndependentStaticFingerprint.WriteTo(writer);
            }
            else
            {
                writer.Write(false);
            }

            writer.End();
        }

        internal abstract void InternalSerialize(PipWriter writer);
        #endregion
       
        /// <summary>
        /// Gets a friendly description of the pip.
        /// </summary>
        /// <remarks>
        /// By no means is this a unique identifier for this instance. It is merely for UI, reporting and light-weight debugging
        /// purposes. This value may be empty, but is non-null.
        /// </remarks>
        [SuppressMessage("Microsoft.Design", "CA1011")]
        [SuppressMessage("Microsoft.Performance", "CA1800:DoNotCastUnnecessarily", Justification = "The code would be too ugly or way more casts would happen if we always cast to process in each invocation.")]
        public string GetDescription(PipExecutionContext context)
        {
            Contract.Requires(context != null);

            var stringTable = context.StringTable;
            var pathTable = context.PathTable;
            
            var p = Provenance;

            using (PooledObjectWrapper<StringBuilder> wrapper = Pools.StringBuilderPool.GetInstance())
            {
                StringBuilder sb = wrapper.Instance;

                if (p == null)
                {
                    sb.Append("Pip<unknown>");
                }
                else
                {
                    sb.Append(FormattedSemiStableHash);

                    if (PipType == PipType.Process)
                    {
                        var process = (Process)this;
                        sb.Append(", ");

                        // Check whether there is a description that should become the remaining description, and in that case let that handle the remaining string and return
                        if (p.UsageIsFullDisplayString && p.Usage.IsValid)
                        {
                            // custom pip description supplied by a customer
                            sb.Append(Utilities.Tracing.FormattingEventListener.CustomPipDescriptionMarker);
                            sb.Append(p.Usage.ToString(pathTable));

                            return sb.ToString();
                        }

                        sb.Append(process.GetToolName(pathTable).ToString(stringTable));

                        if (process.ToolDescription.IsValid)
                        {
                            sb.Append(" (");
                            sb.Append(stringTable.GetString(process.ToolDescription));
                            sb.Append(')');
                        }
                    }
                }

                switch (PipType)
                {
                    case PipType.CopyFile:
                        sb.Append(", <COPYFILE>");
                        break;
                    case PipType.WriteFile:
                        sb.Append(", <WRITEFILE>");
                        break;
                    case PipType.Ipc:
                        sb.Append(", <IPC>");
                        break;
                    case PipType.HashSourceFile:
                        sb.Append(", <HASHFILE>");
                        break;
                    case PipType.SealDirectory:
                        sb.Append(", <SEALDIRECTORY>");
                        break;
                    case PipType.Value:
                        sb.Append(", <VALUE>");
                        break;
                    case PipType.SpecFile:
                        sb.Append(", <SPECFILE>");
                        break;
                    case PipType.Module:
                        sb.Append(", <MODULE>");
                        break;
                }

                if (p != null)
                {
                    if (p.ModuleName.IsValid)
                    {
                        sb.Append(", ");
                        sb.Append(p.ModuleName.ToString(stringTable));
                    }

                    if (context.SymbolTable != null)
                    {
                        if (p.OutputValueSymbol.IsValid)
                        {
                            sb.Append(", ");
                            sb.Append(p.OutputValueSymbol, context.SymbolTable);
                        }
                    }

                    if (p.QualifierId.IsValid)
                    {
                        sb.Append(", ");
                        sb.Append(context.QualifierTable.GetCanonicalDisplayString(p.QualifierId));
                    }
                }

                switch (PipType)
                {
                    case PipType.CopyFile:
                        var copyFilePip = this as CopyFile;
                        sb.Append(", ");
                        sb.Append(copyFilePip.Source.Path.ToString(pathTable));
                        sb.Append(", => ");
                        sb.Append(stringTable.GetString(pathTable.GetFinalComponent(copyFilePip.Destination.Path.Value)));
                        break;
                    case PipType.WriteFile:
                        var write = this as WriteFile;
                        sb.Append(", => ");
                        sb.Append(stringTable.GetString(pathTable.GetFinalComponent(write.Destination.Path.Value)));
                        break;
                    case PipType.Ipc:
                        var ipcPip = this as IpcPip;
                        sb.Append(", moniker id \"").Append(ipcPip.IpcInfo.IpcMonikerId.ToString(pathTable.StringTable));
                        sb.Append("\", config ").Append(ipcPip.IpcInfo.IpcClientConfig.ToJson());
                        sb.Append(", => ");
                        sb.Append(stringTable.GetString(pathTable.GetFinalComponent(ipcPip.OutputFile.Path.Value)));
                        break;
                    case PipType.HashSourceFile:
                        var hash = this as HashSourceFile;
                        sb.Append(", => ");
                        sb.Append(stringTable.GetString(pathTable.GetFinalComponent(hash.Artifact.Path.Value)));
                        break;
                    case PipType.SealDirectory:
                        var seal = this as SealDirectory;
                        sb.Append(", => ");

                        // Note that we cannot access seal.Directory here without assuming that it is fully initialized (IsInitialized)
                        // DirectoryRoot, however, is available beforehand.
                        sb.Append(stringTable.GetString(pathTable.GetFinalComponent(seal.DirectoryRoot.Value)));
                        sb.AppendFormat("({0} entries)", seal.Contents.Length);
                        break;
                    case PipType.Process:
                        var process = this as Process;
                        var semaphores = process.Semaphores;
                        if (semaphores.Length > 0)
                        {
                            sb.Append(", acquires semaphores (");
                            for (int i = 0; i < semaphores.Length; i++)
                            {
                                var s = semaphores[i];
                                sb.Append(i == 0 ? string.Empty : ", ");
                                sb.Append(stringTable.GetString(s.Name));
                                sb.Append(':');
                                sb.Append(s.Value);
                            }

                            sb.Append(")");
                        }

                        break;
                    case PipType.Value:
                        var valuePip = this as ValuePip;
                        sb.Append(", ");
                        sb.Append(valuePip.Symbol, context.SymbolTable);
                        sb.Append(" @ ");
                        sb.Append(valuePip.Qualifier.Id.ToString(CultureInfo.InvariantCulture));
                        break;
                    case PipType.SpecFile:
                        var specFilePip = this as SpecFilePip;
                        sb.Append(", ");
                        sb.Append(specFilePip.SpecFile.Path.ToString(pathTable));
                        break;
                    case PipType.Module:
                        var modulePip = this as ModulePip;
                        sb.Append(", ");
                        sb.Append(modulePip.Identity.ToString(stringTable));
                        break;
                }

                if (p != null)
                {
                    if (p.Usage.IsValid)
                    {
                        // custom pip description supplied by a customer
                        sb.Append(BuildXL.Utilities.Tracing.FormattingEventListener.CustomPipDescriptionMarker);
                        sb.Append(p.Usage.ToString(pathTable));
                    }
                }

                return sb.ToString();
            }
        }

        /// <summary>
        /// Gets a short description of this pip
        /// </summary>
        public string GetShortDescription(PipExecutionContext context, bool withQualifer = true)
        {
            if (Provenance == null)
            {
                return "";
            }

            if (Provenance.Usage.IsValid)
            {
                return Provenance.Usage.ToString(context.PathTable);
            }

            var maybeModuleName = Provenance.ModuleName.IsValid
                ? Provenance.ModuleName.ToString(context.StringTable) + " - "
                : string.Empty;

            var valueName = Provenance.OutputValueSymbol.ToString(context.SymbolTable);

            var toolName = string.Empty;
            if (this is Process process)
            {
                toolName = " - " + process.GetToolName(context.PathTable).ToString(context.StringTable);
            }

            if (!withQualifer)
            {
                return $"{maybeModuleName}{valueName}{toolName}";
            }

            var qualifierName = context.QualifierTable.GetFriendlyUserString(Provenance.QualifierId);

            return $"{maybeModuleName}{valueName}{toolName} [{qualifierName}]";
        }
    }
}
