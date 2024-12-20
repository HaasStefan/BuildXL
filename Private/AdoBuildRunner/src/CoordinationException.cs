﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.CodeAnalysis;
using BuildXL.AdoBuildRunner.Vsts;


namespace BuildXL.AdoBuildRunner
{
    /// <summary>
    /// Custom exception type to be thrown when encountering failing conditions in the build coordination stages
    /// </summary>
    public class CoordinationException : Exception
    {
        /// <inheritdoc />
        public CoordinationException(string message) : base(message) { }

        /// <inheritdoc />
        public CoordinationException(Exception inner) : base(inner.Message, inner) { }

        /// <summary>
        /// Logs an error message in the logger and throws a CoordinationException with the same message
        /// </summary>
        [DoesNotReturn]
        public static void LogAndThrow(ILogger logger, string message)
        {
            logger?.Error(message);
            throw new CoordinationException(message);
        }
    }
}
