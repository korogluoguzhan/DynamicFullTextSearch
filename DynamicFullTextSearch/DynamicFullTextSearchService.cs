using Microsoft.EntityFrameworkCore;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace DynamicFullTextSearch
{
    public class DynamicFullTextSearchService<TContext> where TContext : DbContext
    {
        private readonly TContext _context;

        public DynamicFullTextSearchService(TContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>
        /// Performs Full-Text Search and Regex search.
        /// </summary>
        /// <typeparam name="TEntity">Entity type</typeparam>
        /// <param name="searchKeyword">Search keyword</param>
        /// <param name="language">Language (default: english)</param>
        /// <param name="properties">Properties to search</param>
        /// <returns>List of entities</returns>
        public IQueryable<TEntity> DynamicSearch<TEntity>(
            string searchKeyword,
            string language = "english",
            params Expression<Func<TEntity, object>>[] properties) where TEntity : class
        {
            if (string.IsNullOrWhiteSpace(searchKeyword))
                throw new ArgumentException("Search keyword cannot be null or whitespace.", nameof(searchKeyword));

            if (properties == null || properties.Length == 0)
                throw new ArgumentException("At least one property must be provided.", nameof(properties));

            if (language.ToLower() == "turkish")
            {
                searchKeyword = ConvertTurkishCharacters(searchKeyword);
            }

            var normalizedKeyword = searchKeyword.ToLower();

            string dynamicQuery = BuildTsQuery(searchKeyword);

            var whereConditions = new List<string>();

            foreach (var property in properties)
            {
                var columnName = GetColumnName(property);

                var tsVectorCondition = $"to_tsvector('{language}', \"{columnName}\") @@ plainto_tsquery('{language}', @0)";

                var regexCondition = $"\"{columnName}\" ~* @1";

                whereConditions.Add($"({tsVectorCondition} OR {regexCondition})");
            }

            var whereClause = string.Join(" OR ", whereConditions);

            var tableName = _context.Model.FindEntityType(typeof(TEntity)).GetTableName();

            var sqlQuery = $"SELECT * FROM \"{tableName}\" WHERE {whereClause}";

            var query = _context.Set<TEntity>().FromSqlRaw(sqlQuery,
            new NpgsqlParameter("@0", NpgsqlTypes.NpgsqlDbType.Text) { Value = dynamicQuery },
            new NpgsqlParameter("@1", NpgsqlTypes.NpgsqlDbType.Text) { Value = normalizedKeyword });

            return query;
        }

        /// <summary>
        /// Builds a dynamic to_tsquery string from the provided search keyword.
        /// </summary>
        /// <param name="searchKeyword">The keyword to build the to_tsquery from.</param>
        /// <returns>A string representing the dynamic to_tsquery.</returns>
        private static string BuildTsQuery(string searchKeyword)
        {
            if (string.IsNullOrEmpty(searchKeyword))
            {
                return string.Empty;
            }

            string dynamicQuery = string.Join(" & ", searchKeyword.Split(' ').Select(word => $"{word}:*"));

            return dynamicQuery;
        }


        /// <summary>
        /// Retrieves the column name from the provided property expression.
        /// </summary>
        /// <typeparam name="TEntity">The type of the entity.</typeparam>
        /// <param name="propertyExpression">The expression representing the property.</param>
        /// <returns>The name of the property as a string.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the provided expression is not valid.</exception>
        private static string GetColumnName<TEntity>(Expression<Func<TEntity, object>> propertyExpression)
        {
            if (propertyExpression.Body is MemberExpression member)
                return member.Member.Name;

            if (propertyExpression.Body is UnaryExpression unary && unary.Operand is MemberExpression memberExpression)
                return memberExpression.Member.Name;

            throw new InvalidOperationException("Invalid property expression.");
        }

        /// <summary>
        /// Converts Turkish characters in the input string to their English equivalents.
        /// </summary>
        /// <param name="input">The input string containing Turkish characters.</param>
        /// <returns>A string with Turkish characters replaced by English characters.</returns>
        private static string ConvertTurkishCharacters(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            var replacements = new Dictionary<char, char>
            {
                { 'ı', 'i' }, { 'İ', 'I' },
                { 'ş', 's' }, { 'Ş', 'S' },
                { 'ğ', 'g' }, { 'Ğ', 'G' },
                { 'ü', 'u' }, { 'Ü', 'U' },
                { 'ö', 'o' }, { 'Ö', 'O' },
                { 'ç', 'c' }, { 'Ç', 'C' }
            };

            var builder = new StringBuilder(input.Length);
            foreach (var c in input)
            {
                builder.Append(replacements.ContainsKey(c) ? replacements[c] : c);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Asynchronously adds the Trigger function to the database with a dynamic language parameter.
        /// </summary>
        /// <param name="tableName">The name of the table to which the trigger will be added.</param>
        /// <param name="language">The language used for the full-text search.</param>
        /// <param name="columns">An array of column names to be included in the search vector.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task CreateTriggerForSearchVectorAsync(string tableName, string language, string[] columns)
        {
            try
            {
                string columnsConcatenation = string.Join(" || ' ' || ", columns.Select(c => $"COALESCE(NEW.\"{c}\", '')"));

                string triggerFunction = $@"
                CREATE OR REPLACE FUNCTION update_search_vector()
                RETURNS trigger AS $$ 
                BEGIN 
                    NEW.search_vector := to_tsvector('{language}', {columnsConcatenation}); 
                    RETURN NEW; 
                END; 
                $$ LANGUAGE plpgsql;";

                await _context.Database.ExecuteSqlRawAsync(triggerFunction);

                string triggerSql = $@"
                CREATE TRIGGER trigger_update_search_vector 
                BEFORE INSERT OR UPDATE ON ""{tableName}"" 
                FOR EACH ROW EXECUTE FUNCTION update_search_vector();";

                await _context.Database.ExecuteSqlRawAsync(triggerSql);

                string updateSql = $@"
                UPDATE ""{tableName}"" 
                SET ""search_vector"" = to_tsvector('{language}', COALESCE({string.Join(" || ' ' || ", columns.Select(c => $"\"{c}\""))})) 
                WHERE ""search_vector"" IS NULL;";

                await _context.Database.ExecuteSqlRawAsync(updateSql);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("An error occurred during the Trigger and update operation.", ex);
            }
        }

        /// <summary>
        /// Asynchronously adds the index for Full-Text Search to the database.
        /// </summary>
        /// <param name="tableName">The name of the table for which the search vector index will be created.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task CreateSearchVectorIndexAsync(string tableName)
        {
            try
            {
                string indexSql = $@"
                CREATE INDEX IF NOT EXISTS idx_search_vector ON ""{tableName}"" USING GIN (search_vector);";

                await _context.Database.ExecuteSqlRawAsync(indexSql);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("An error occurred while creating the search vector index.", ex);
            }
        }
    }
}
