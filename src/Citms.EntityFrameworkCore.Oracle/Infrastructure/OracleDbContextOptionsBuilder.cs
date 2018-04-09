// Copyright (c) Pomelo Foundation. All rights reserved.
// Licensed under the MIT. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using EFCore.Oracle.Infrastructure.Internal;
using JetBrains.Annotations;

namespace Microsoft.EntityFrameworkCore.Infrastructure
{
    public class OracleDbContextOptionsBuilder : RelationalDbContextOptionsBuilder<OracleDbContextOptionsBuilder, OracleOptionsExtension>
    {
        public OracleDbContextOptionsBuilder([NotNull] DbContextOptionsBuilder optionsBuilder)
            : base(optionsBuilder)
        {
        }
        /// <summary>
        ///     Configures the context to use the default retrying <see cref="IExecutionStrategy" />.
        /// </summary>
        public virtual OracleDbContextOptionsBuilder EnableRetryOnFailure()
            => ExecutionStrategy(c => new OracleRetryingExecutionStrategy(c));

        /// <summary>
        ///     Configures the context to use the default retrying <see cref="IExecutionStrategy" />.
        /// </summary>
        public virtual OracleDbContextOptionsBuilder EnableRetryOnFailure(int maxRetryCount)
            => ExecutionStrategy(c => new OracleRetryingExecutionStrategy(c, maxRetryCount));

        /// <summary>
        ///     Configures the context to use the default retrying <see cref="IExecutionStrategy" />.
        /// </summary>
        /// <param name="maxRetryCount"> The maximum number of retry attempts. </param>
        /// <param name="maxRetryDelay"> The maximum delay between retries. </param>
        /// <param name="errorNumbersToAdd"> Additional error codes that should be considered transient. </param>
        public virtual OracleDbContextOptionsBuilder EnableRetryOnFailure(
            int maxRetryCount,
            TimeSpan maxRetryDelay,
            [NotNull] ICollection<int> errorNumbersToAdd)
            => ExecutionStrategy(c => new OracleRetryingExecutionStrategy(c, maxRetryCount, maxRetryDelay, errorNumbersToAdd));
    }
}
