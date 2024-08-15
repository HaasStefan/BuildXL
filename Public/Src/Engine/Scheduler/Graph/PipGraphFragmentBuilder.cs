﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Threading;
using BuildXL.Ipc.Common;
using BuildXL.Pips;
using BuildXL.Pips.Builders;
using BuildXL.Pips.Graph;
using BuildXL.Pips.Operations;
using BuildXL.Utilities;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using System.Diagnostics.CodeAnalysis;
using BuildXL.Scheduler.Fingerprints;

namespace BuildXL.Scheduler.Graph
{
    /// <summary>
    /// Class for building graph fragments.
    /// </summary>
    public class PipGraphFragmentBuilder : IMutablePipGraph, IPipScheduleTraversal
    {
        /// <summary>
        /// Seal directory table
        /// </summary>
        protected readonly SealedDirectoryTable SealDirectoryTable;

        /// <summary>
        /// Configuration
        /// </summary>
        protected readonly IConfiguration Configuration;

        /// <summary>
        /// File producers.
        /// </summary>
        protected readonly ConcurrentBigMap<FileArtifact, PipId> FileProducers = new ConcurrentBigMap<FileArtifact, PipId>();

        /// <summary>
        /// Opaque directory producers.
        /// </summary>
        protected readonly ConcurrentBigMap<DirectoryArtifact, PipId> OpaqueDirectoryProducers = new ConcurrentBigMap<DirectoryArtifact, PipId>();

        private readonly PipStaticFingerprinter m_pipStaticFingerprinter;
        
        private readonly PipExecutionContext m_pipExecutionContext;
        private readonly ConcurrentQueue<Pip> m_pips = new ConcurrentQueue<Pip>();
        private readonly ConcurrentBigMap<PipId, SealDirectoryKind> m_sealDirectoryPips = new ConcurrentBigMap<PipId, SealDirectoryKind>();
        private readonly ConcurrentBigMap<DirectoryArtifact, HashSet<FileArtifact>> m_outputsUnderOpaqueExistenceAssertions = new ConcurrentBigMap<DirectoryArtifact, HashSet<FileArtifact>>();
        private readonly Lazy<IpcMoniker> m_lazyApiServerMoniker;
        private PipGraph.WindowsOsDefaults m_windowsOsDefaults;
        private PipGraph.UnixDefaults m_unixDefaults;
        private readonly object m_osDefaultLock = new object();
        private int m_nextPipId = 0;

        /// <summary>
        /// Creates an instance of <see cref="PipGraphFragmentBuilder"/>.
        /// </summary>
        public PipGraphFragmentBuilder(
            PipExecutionContext pipExecutionContext, 
            IConfiguration configuration,
            PathExpander pathExpander)
        {
            Contract.Requires(pipExecutionContext != null);

            Configuration = configuration;
            m_pipExecutionContext = pipExecutionContext;
            m_lazyApiServerMoniker = configuration.Schedule.UseFixedApiServerMoniker
                ? Lazy.Create(() => IpcMoniker.GetFixedMoniker())
                : Lazy.Create(() => IpcMoniker.CreateNew());
            SealDirectoryTable = new SealedDirectoryTable(m_pipExecutionContext.PathTable);

            if (configuration.Schedule.ComputePipStaticFingerprints)
            {
                var extraFingerprintSalts = new ExtraFingerprintSalts(
                    configuration,
                    configuration.Cache.CacheSalt,
                    new DirectoryMembershipFingerprinterRuleSet(configuration, pipExecutionContext.StringTable).ComputeSearchPathToolsHash(),
                    ObservationReclassifier.ComputeObservationReclassificationRulesHash(Configuration));
                var pipSpecificPropertiesConfig = new PipSpecificPropertiesConfig(configuration.Engine.PipSpecificPropertyAndValues);

                m_pipStaticFingerprinter = new PipStaticFingerprinter(
                    pipExecutionContext.PathTable,
                    sealDirectoryFingerprintLookup: null,
                    directoryProducerFingerprintLookup: null,
                    extraFingerprintSalts: extraFingerprintSalts,
                    pathExpander: pathExpander,
                    pipFingerprintSaltLookup: process => pipSpecificPropertiesConfig.GetPipSpecificPropertyValue(PipSpecificPropertiesConfig.PipSpecificProperty.PipFingerprintSalt, process.SemiStableHash))
                {
                    FingerprintTextEnabled = configuration.Schedule.LogPipStaticFingerprintTexts
                };
            }
        }

        /// <inheritdoc />
        public int PipCount => m_pips.Count;

        private void AddPip(Pip pip)
        {
            m_pips.Enqueue(pip);
            pip.PipId = new PipId((uint)Interlocked.Increment(ref m_nextPipId));
            
            if (pip.PipType == PipType.SealDirectory)
            {
                m_sealDirectoryPips.TryAdd(pip.PipId, ((SealDirectory)pip).Kind);
            }
        }

        /// <inheritdoc />
        public virtual bool AddCopyFile([NotNull] CopyFile copyFile, PipId valuePip)
        {
            AddPip(copyFile);
            FileProducers[copyFile.Destination] = copyFile.PipId;
            ComputeStaticFingerprint(copyFile);
            return true;
        }

        /// <inheritdoc />
        public virtual bool AddIpcPip([NotNull] IpcPip ipcPip, PipId valuePip)
        {
            AddPip(ipcPip);
            FileProducers[ipcPip.OutputFile] = ipcPip.PipId;
            return true;
        }

        /// <inheritdoc />
        public bool AddModule([NotNull] ModulePip module)
        {
            AddPip(module);
            return true;
        }

        /// <inheritdoc />
        public bool AddModuleModuleDependency(ModuleId moduleId, ModuleId dependency) => true;

        /// <inheritdoc />
        public bool AddOutputValue([NotNull] ValuePip value)
        {
            AddPip(value);
            return true;
        }

        /// <inheritdoc />
        public virtual bool AddProcess([NotNull] Process process, PipId valuePip)
        {
            AddPip(process);

            foreach (var fileOutput in process.FileOutputs)
            {
                FileProducers[fileOutput.ToFileArtifact()] = process.PipId;
            }

            foreach (var directoryOutput in process.DirectoryOutputs)
            {
                OpaqueDirectoryProducers[directoryOutput] = process.PipId;
            }

            ComputeStaticFingerprint(process);

            return true;
        }

        /// <inheritdoc />
        public virtual DirectoryArtifact AddSealDirectory([NotNull] SealDirectory sealDirectory, PipId valuePip)
        {
            AddPip(sealDirectory);
            DirectoryArtifact artifactForNewSeal;

            if (sealDirectory.Kind == SealDirectoryKind.SharedOpaque)
            {
                Contract.Assume(sealDirectory.Directory.IsSharedOpaque);
                artifactForNewSeal = sealDirectory.Directory;
            }
            else
            {
                // For the regular dynamic case, the directory artifact is always
                // created with sealId 0. For other cases, we reserve it
                artifactForNewSeal = sealDirectory.Kind == SealDirectoryKind.Opaque
                    ? DirectoryArtifact.CreateWithZeroPartialSealId(sealDirectory.DirectoryRoot)
                    : SealDirectoryTable.ReserveDirectoryArtifact(sealDirectory);
                sealDirectory.SetDirectoryArtifact(artifactForNewSeal);
            }

            SealDirectoryTable.AddSeal(sealDirectory);
            ComputeStaticFingerprint(sealDirectory);

            return artifactForNewSeal;
        }

        /// <inheritdoc />
        public bool AddSpecFile([NotNull] SpecFilePip specFile)
        {
            AddPip(specFile);
            return true;
        }

        /// <inheritdoc />
        public bool AddValueValueDependency(in ValuePip.ValueDependency valueDependency) => true;

        /// <inheritdoc />
        public virtual bool AddWriteFile([NotNull] WriteFile writeFile, PipId valuePip)
        {
            AddPip(writeFile);
            FileProducers[writeFile.Destination] = writeFile.PipId;
            ComputeStaticFingerprint(writeFile);

            return true;
        }

        /// <inheritdoc />
        public bool ApplyCurrentOsDefaults(ProcessBuilder processBuilder)
        {
            // TODO: This is a copy from PipGraph.Builder. Refactor it!
            if (OperatingSystemHelper.IsUnixOS)
            {
                if (m_unixDefaults == null)
                {
                    lock (m_osDefaultLock)
                    {
                        if (m_unixDefaults == null)
                        {
                            m_unixDefaults = new PipGraph.UnixDefaults(m_pipExecutionContext.PathTable, this);
                        }
                    }
                }

                return m_unixDefaults.ProcessDefaults(processBuilder);
            }
            else
            {
                if (m_windowsOsDefaults == null)
                {
                    lock (m_osDefaultLock)
                    {
                        if (m_windowsOsDefaults == null)
                        {
                            m_windowsOsDefaults = new PipGraph.WindowsOsDefaults(m_pipExecutionContext.PathTable);
                        }
                    }
                }

                return m_windowsOsDefaults.ProcessDefaults(processBuilder);
            }
        }

        /// <inheritdoc />
        public IpcMoniker GetApiServerMoniker() => m_lazyApiServerMoniker.Value;

        /// <inheritdoc />
        public GraphPatchingStatistics PartiallyReloadGraph([NotNull] HashSet<AbsolutePath> affectedSpecs) => default;

        /// <inheritdoc />
        public DirectoryArtifact ReserveSharedOpaqueDirectory(AbsolutePath directoryArtifactRoot) => SealDirectoryTable.CreateSharedOpaqueDirectoryWithNewSealId(directoryArtifactRoot);

        /// <inheritdoc />
        public IEnumerable<Pip> RetrievePipImmediateDependencies(Pip pip) => throw new NotImplementedException();

        /// <inheritdoc />
        public virtual IEnumerable<Pip> RetrievePipImmediateDependents(Pip pip) => throw new NotImplementedException();

        /// <inheritdoc />
        public IEnumerable<Pip> RetrieveScheduledPips() => m_pips;

        /// <inheritdoc />
        public void SetSpecsToIgnore(IEnumerable<AbsolutePath> specsToIgnore)
        {
        }

        private void ComputeStaticFingerprint(Pip pip)
        {
            if (m_pipStaticFingerprinter != null)
            {
                pip.IndependentStaticFingerprint = m_pipStaticFingerprinter.ComputeWeakFingerprint(pip).Hash;
            }
        }

        /// <inheritdoc/>
        public bool TryGetSealDirectoryKind(DirectoryArtifact directoryArtifact, out SealDirectoryKind kind)
        {
            Contract.Requires(directoryArtifact.IsValid);

            kind = default(SealDirectoryKind);
            if (!SealDirectoryTable.TryGetSealForDirectoryArtifact(directoryArtifact, out var pipId))
            {
                return false;
            }

            var result = m_sealDirectoryPips.TryGet(pipId);

            if (result.IsFound)
            {
                kind = result.Item.Value;
                return true;
            }

            return false;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Current implementation is O(n) but this method is for now only called in test contexts.
        /// </remarks>
        public Pip GetPipFromPipId(PipId pipId)
        {
            return m_pips.FirstOrDefault(pip => pip.PipId == pipId);
        }

        /// <inheritdoc/>
        public bool TryAssertOutputExistenceInOpaqueDirectory(DirectoryArtifact outputDirectoryArtifact, AbsolutePath outputInOpaque, out FileArtifact fileArtifact)
        {
            Contract.Requires(outputDirectoryArtifact.IsValid);
            Contract.Requires(outputDirectoryArtifact.IsOutputDirectory());
            Contract.Requires(outputInOpaque.IsWithin(m_pipExecutionContext.PathTable, outputDirectoryArtifact.Path));
            fileArtifact = FileArtifact.CreateOutputFile(outputInOpaque);

            var producerResult = OpaqueDirectoryProducers.TryGet(outputDirectoryArtifact);
            if (producerResult.IsFound)
            {
                FileProducers.TryAdd(fileArtifact, producerResult.Item.Value);
            }
            else
            {
                m_outputsUnderOpaqueExistenceAssertions.AddOrUpdate(
                    outputDirectoryArtifact,
                    fileArtifact,
                    (key, fileArtifact) => new HashSet<FileArtifact> { fileArtifact },
                    (key, fileArtifact, assertions) => { assertions.Add(fileArtifact); return assertions; });
            }
            
            return true;
        }

        /// <inheritdoc/>
        public IReadOnlyCollection<KeyValuePair<DirectoryArtifact, HashSet<FileArtifact>>> RetrieveOutputsUnderOpaqueExistenceAssertions()
        {
            return m_outputsUnderOpaqueExistenceAssertions;
        }
    }
}
