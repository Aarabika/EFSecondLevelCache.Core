#if NETSTANDARD2_1
using System.Linq;
using System.Linq.Expressions;
using EFSecondLevelCache.Core.Contracts;
using System;
using CacheManager.Core;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace EFSecondLevelCache.Core
{
    /// <summary>
    /// A custom cache key provider for EF queries.
    /// </summary>
    public class EFCacheKeyProvider : IEFCacheKeyProvider
    {
        private static readonly TypeInfo _queryCompilerTypeInfo = typeof(QueryCompiler).GetTypeInfo();

        private static readonly FieldInfo _queryCompilerField =
            typeof(EntityQueryProvider).GetTypeInfo().DeclaredFields.First(x => x.Name == "_queryCompiler");

        private static readonly FieldInfo _queryContextFactoryField =
            _queryCompilerTypeInfo.DeclaredFields.First(x => x.Name == "_queryContextFactory");

        private static readonly FieldInfo _loggerField =
            _queryCompilerTypeInfo.DeclaredFields.First(x => x.Name == "_logger");

        private static readonly TimeSpan _slidingExpirationTimeSpan = TimeSpan.FromMinutes(7);

        private static readonly ICacheManager<string> _keysCacheManager =
            EFStaticServiceProvider.Instance.GetRequiredService<ICacheManager<string>>();

        private readonly IEFCacheKeyHashProvider _cacheKeyHashProvider;

        /// <summary>
        /// A custom cache key provider for EF queries.
        /// </summary>
        /// <param name="cacheKeyHashProvider">Provides the custom hashing algorithm.</param>
        public EFCacheKeyProvider(IEFCacheKeyHashProvider cacheKeyHashProvider)
        {
            _cacheKeyHashProvider = cacheKeyHashProvider;
        }

        /// <summary>
        /// Gets an EF query and returns its hashed key to store in the cache.
        /// </summary>
        /// <typeparam name="T">Type of the entity</typeparam>
        /// <param name="query">The EF query.</param>
        /// <param name="expression">An expression tree that represents a LINQ query.</param>
        /// <param name="saltKey">If you think the computed hash of the query is not enough, set this value.</param>
        /// <returns>Information of the computed key of the input LINQ query.</returns>
        public EFCacheKey GetEFCacheKey<T>(IQueryable<T> query, Expression expression, string saltKey = "")
        {
            var expressionVisitorResult = EFQueryExpressionVisitor.GetDebugView(expression);
            var sqlData = toSql(query, expression, _cacheKeyHashProvider);
            var key = $"{sqlData};{expressionVisitorResult.DebugView};{saltKey}";
            var keyHash = _cacheKeyHashProvider.ComputeHash(key);
            return new EFCacheKey
            {
                Key = key,
                KeyHash = keyHash,
                CacheDependencies = expressionVisitorResult.Types
            };
        }

        private static string toSql<TEntity>(
            IQueryable<TEntity> query,
            Expression expression,
            IEFCacheKeyHashProvider cacheKeyHashProvider)
        {
            var queryCompiler = (QueryCompiler)_queryCompilerField.GetValue(query.Provider);
            var (expressionKeyHash, modifiedExpression) =
                getExpressionKeyHash(queryCompiler, cacheKeyHashProvider, expression);

            var cachedSql = _keysCacheManager.Get<string>(expressionKeyHash);
            if (cachedSql != null)
            {
                return cachedSql;
            }

            var expressionPrinter = new ExpressionPrinter();
            expressionPrinter.Visit(modifiedExpression);
            var sql = expressionPrinter.StringBuilder.ToString();
            setCache(expressionKeyHash, sql);
            return sql;
        }

        private static void setCache(string expressionKeyHash, string sql)
        {
            _keysCacheManager.Add(
                new CacheItem<string>(expressionKeyHash, sql, ExpirationMode.Sliding, _slidingExpirationTimeSpan));
        }

        private static (string ExpressionKeyHash, Expression ModifiedExpression) getExpressionKeyHash(
            QueryCompiler queryCompiler,
            IEFCacheKeyHashProvider cacheKeyHashProvider,
            Expression expression)
        {
            var queryContextFactory = (IQueryContextFactory)_queryContextFactoryField.GetValue(queryCompiler);
            var queryContext = queryContextFactory.Create();
            var logger = (IDiagnosticsLogger<DbLoggerCategory.Query>)_loggerField.GetValue(queryCompiler);
            expression = queryCompiler.ExtractParameters(expression, queryContext, logger, parameterize: false);

            var expressionKey = $"{ExpressionEqualityComparer.Instance.GetHashCode(expression)};";
            var parameterValues = queryContext.ParameterValues;
            if (parameterValues.Any())
            {
                expressionKey = parameterValues.Aggregate(expressionKey,
                    (current, item) => current + $"{item.Key}={item.Value?.GetHashCode()};");
            }

            return (cacheKeyHashProvider.ComputeHash(expressionKey), expression);
        }
    }
}
#endif