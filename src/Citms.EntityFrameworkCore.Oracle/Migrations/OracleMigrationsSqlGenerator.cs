// Copyright (c) Pomelo Foundation. All rights reserved.
// Licensed under the MIT. See LICENSE in the project root for license information.

using System;
using System.Linq;
using System.Text.RegularExpressions;
using EFCore.Oracle.Infrastructure.Internal;
using EFCore.Oracle.Metadata.Internal;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Utilities;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore.Migrations
{
    public class OracleMigrationsSqlGenerator : MigrationsSqlGenerator
    {
        private static readonly Regex _typeRe = new Regex(@"([a-z0-9]+)\s*?(?:\(\s*(\d+)?\s*\))?", RegexOptions.IgnoreCase);
        private readonly IOracleOptions _options;

        public OracleMigrationsSqlGenerator(
            [NotNull] MigrationsSqlGeneratorDependencies dependencies,
            [NotNull] IOracleOptions options)
            : base(dependencies)
        {
            _options = options;
        }

        protected override void Generate([NotNull] MigrationOperation operation, [CanBeNull] IModel model, [NotNull] MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            if (operation is OracleCreateDatabaseOperation createDatabaseOperation)
            {
                Generate(createDatabaseOperation, model, builder);
                builder.EndCommand();
                return;
            }

            var dropDatabaseOperation = operation as OracleDropDatabaseOperation;
            if (dropDatabaseOperation is OracleDropDatabaseOperation)
            {
                Generate(dropDatabaseOperation, model, builder);
                builder.EndCommand();
                return;
            }

            base.Generate(operation, model, builder);
        }

        protected override void Generate(DropColumnOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            var identifier = Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema);
            var alterBase = $"ALTER TABLE {identifier} DROP COLUMN {Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name)}";
            builder.Append(alterBase).Append(Dependencies.SqlGenerationHelper.StatementTerminator);
            EndStatement(builder);
        }

        private bool columnSupportsDefault(string columnType)
        {
            if (string.IsNullOrWhiteSpace(columnType))
                return false;

            switch (columnType.ToLower())
            {
                // blob
                case "tinyblob":
                case "blob":
                case "mediumblob":
                case "longblob":
                // text
                case "tinytext":
                case "text":
                case "mediumtext":
                case "longtext":
                // geometry
                case "geometry":
                case "point":
                case "linestring":
                case "polygon":
                case "multipoint":
                case "multilinestring":
                case "multipolygon":
                case "geometrycollection":
                // json
                case "json":
                    return false;

                default:
                    return true;
            }
        }

        protected override void Generate(AlterColumnOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            var newColumnType = operation.ColumnType;
            if (newColumnType == null)
            {
                var property = FindProperty(model, operation.Schema, operation.Table, operation.Name);
                newColumnType = property != null
                    ? Dependencies.TypeMapper.GetMapping(property).StoreType
                    : Dependencies.TypeMapper.GetMapping(operation.ClrType).StoreType;
            }
            var oldColumnType = operation.OldColumn?.ColumnType;

            var identifier = Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema);

            // default value
            var defaultBase = $"ALTER TABLE {identifier} ALTER COLUMN {Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name)}";
            var hasDefaultValue = operation.DefaultValue != null;
            var hasDefaultSql = !string.IsNullOrWhiteSpace(operation.DefaultValueSql);
            var hasDefault = hasDefaultValue || hasDefaultSql;

            // drop old default value if supported
            if (columnSupportsDefault(oldColumnType))
            {
                builder.Append(defaultBase)
                    .Append(" DROP DEFAULT")
                    .AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
            }

            // alter the column
            var alterBase = $"ALTER TABLE {identifier} MODIFY COLUMN {Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name)}";
            builder.Append(alterBase)
                .Append(" ")
                .Append(newColumnType)
                .Append(operation.IsNullable ? " NULL" : " NOT NULL")
                .AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);

            // set default value after altering column if new column definition has a default value
            if (hasDefault)
            {
                if (!columnSupportsDefault(newColumnType))
                    throw new NotSupportedException($"{newColumnType} column can't have a default value");

                if (hasDefaultValue)
                {
                    var typeMapping = Dependencies.TypeMapper.GetMapping(operation.DefaultValue.GetType());
                    builder.Append(defaultBase)
                        .Append(" SET DEFAULT ")
                        .Append(typeMapping.GenerateSqlLiteral(operation.DefaultValue))
                        .AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
                }
                else if (hasDefaultSql)
                {
                    builder.Append(defaultBase)
                        .Append(" SET DEFAULT ")
                        .Append(operation.DefaultValueSql)
                        .AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
                }
            }

            EndStatement(builder);
        }

        protected override void Generate(CreateSequenceOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            throw new NotSupportedException("Oracle doesn't support sequence operation.");
        }

        protected override void Generate(RenameIndexOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            if (operation.NewName != null)
            {
                if (_options.ConnectionSettings.ServerVersion.SupportsRenameIndex)
                {
                    builder.Append("ALTER TABLE ")
                        .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
                        .Append(" RENAME INDEX ")
                        .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
                        .Append(" TO ")
                        .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName))
                        .AppendLine(";");

                    EndStatement(builder);
                }
                else
                {
                    var createTableSyntax = _options.GetCreateTable(Dependencies.SqlGenerationHelper, operation.Table, operation.Schema);

                    if (createTableSyntax == null)
                        throw new InvalidOperationException($"Could not find SHOW CREATE TABLE syntax for table: '{Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema)}'");

                    var indexDefinitionRe = new Regex($"^\\s*((?:UNIQUE\\s)?KEY\\s)`?{operation.Name}`?(.*)$", RegexOptions.Multiline);
                    var match = indexDefinitionRe.Match(createTableSyntax);

                    string newIndexDefinition;
                    if (match.Success)
                        newIndexDefinition = match.Groups[1].Value + Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName) + " " + match.Groups[2].Value.Trim().TrimEnd(',');
                    else
                        throw new InvalidOperationException($"Could not find column definition for table: '{Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema)}' column: {operation.Name}");

                    builder
                        .Append("ALTER TABLE ")
                        .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
                        .Append(" DROP INDEX ")
                        .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
                        .AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
                    EndStatement(builder);

                    builder
                        .Append("ALTER TABLE ")
                        .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
                        .Append(" ADD ")
                        .Append(newIndexDefinition)
                        .AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
                    EndStatement(builder);
                }
            }
        }

        protected override void Generate(RenameSequenceOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            throw new NotSupportedException("Oracle doesn't support sequence operation.");
        }

        protected override void Generate(RenameTableOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            builder
                .Append("ALTER TABLE")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema))
                .Append(" RENAME ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName, operation.NewSchema));

            EndStatement(builder);
        }

        protected override void Generate([NotNull] CreateIndexOperation operation, [CanBeNull] IModel model, [NotNull] MigrationCommandListBuilder builder, bool terminate)
        {
            var method = (string)operation[OracleAnnotationNames.Prefix];
            var isFullText = !string.IsNullOrEmpty((string)operation[OracleAnnotationNames.FullTextIndex]);
            var isSpatial = !string.IsNullOrEmpty((string)operation[OracleAnnotationNames.SpatialIndex]);

            builder.Append("CREATE ");

            if (operation.IsUnique)
            {
                builder.Append("UNIQUE ");
            }
            else if (isFullText)
            {
                builder.Append("FULLTEXT ");
            }
            else if (isSpatial)
            {
                builder.Append("SPATIAL ");
            }

            builder
                .Append("INDEX ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name.LimitLength(64)))
                .Append(" ON ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema));

            if (method != null)
            {
                builder
                    .Append(" USING ")
                    .Append(method);
            }

            builder
                .Append(" (")
                .Append(ColumnList(operation.Columns))
                .Append(")");

            if (terminate)
            {
                builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
                EndStatement(builder);
            }
        }

        protected override void Generate(
            [NotNull] CreateIndexOperation operation,
            [CanBeNull] IModel model,
            [NotNull] MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            Generate(operation, model, builder, true);
        }

        protected override void Generate(EnsureSchemaOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            builder
                .Append("CREATE DATABASE IF NOT EXISTS ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
                .Append(Dependencies.SqlGenerationHelper.StatementTerminator)
                .AppendLine(Dependencies.SqlGenerationHelper.BatchTerminator);
            EndStatement(builder);
        }

        public virtual void Generate(OracleCreateDatabaseOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            builder
                .Append("CREATE DATABASE ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
                .Append(Dependencies.SqlGenerationHelper.StatementTerminator)
                .AppendLine(Dependencies.SqlGenerationHelper.BatchTerminator);
            EndStatement(builder);
        }

        public virtual void Generate(OracleDropDatabaseOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            builder
                .Append("DROP DATABASE ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
                .Append(Dependencies.SqlGenerationHelper.StatementTerminator)
                .AppendLine(Dependencies.SqlGenerationHelper.BatchTerminator);
            EndStatement(builder);
        }

        protected override void Generate(DropIndexOperation operation, IModel model, MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            builder
                .Append("ALTER TABLE ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
                .Append(" DROP INDEX ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
                .AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);

            EndStatement(builder);
        }

        protected override void Generate(
            [NotNull] RenameColumnOperation operation,
            [CanBeNull] IModel model,
            [NotNull] MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            var createTableSyntax = _options.GetCreateTable(Dependencies.SqlGenerationHelper, operation.Table, operation.Schema);

            if (createTableSyntax == null)
            {
                throw new InvalidOperationException($"Could not find SHOW CREATE TABLE syntax for table: '{Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema)}'");
            }

            var columnDefinitionRe = new Regex($"^\\s*`?{operation.Name}`?\\s(.*)?$", RegexOptions.Multiline);
            var match = columnDefinitionRe.Match(createTableSyntax);

            string columnDefinition;
            if (match.Success)
            {
                columnDefinition = match.Groups[1].Value.Trim().TrimEnd(',');
            }
            else
            {
                throw new InvalidOperationException($"Could not find column definition for table: '{Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema)}' column: {operation.Name}");
            }

            builder.Append("ALTER TABLE ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
                .Append(" CHANGE ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
                .Append(" ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.NewName))
                .Append(" ")
                .Append(columnDefinition);

            EndStatement(builder);
        }

        protected override void ColumnDefinition(
            [CanBeNull] string schema,
            [NotNull] string table,
            [NotNull] string name,
            [NotNull] Type clrType,
            [CanBeNull] string type,
            [CanBeNull] bool? unicode,
            [CanBeNull] int? maxLength,
            bool rowVersion,
            bool nullable,
            [CanBeNull] object defaultValue,
            [CanBeNull] string defaultValueSql,
            [CanBeNull] string computedColumnSql,
            [NotNull] IAnnotatable annotatable,
            [CanBeNull] IModel model,
            [NotNull] MigrationCommandListBuilder builder)
        {
            Check.NotEmpty(name, nameof(name));
            Check.NotNull(annotatable, nameof(annotatable));
            Check.NotNull(clrType, nameof(clrType));
            Check.NotNull(builder, nameof(builder));

            if (type == null)
            {
                type = GetColumnType(schema, table, name, clrType, unicode, maxLength, rowVersion, model);
            }

            var property = FindProperty(model, schema, table, name);

            var matchType = type;
            var matchLen = "";
            var match = _typeRe.Match(type ?? "-");
            if (match.Success)
            {
                matchType = match.Groups[1].Value.ToLower();
                if (!string.IsNullOrWhiteSpace(match.Groups[2].Value))
                {
                    matchLen = match.Groups[2].Value;
                }
            }

            var autoIncrement = false;
            var valueGenerationStrategy = annotatable[OracleAnnotationNames.ValueGenerationStrategy] as OracleValueGenerationStrategy?;
            if (!valueGenerationStrategy.HasValue)
            {
                if (property?.ValueGenerated == ValueGenerated.OnAdd)
                {
                    valueGenerationStrategy = OracleValueGenerationStrategy.IdentityColumn;
                }
                else if (property?.ValueGenerated == ValueGenerated.OnAddOrUpdate)
                {
                    valueGenerationStrategy = OracleValueGenerationStrategy.ComputedColumn;
                }
                else
                {
                    // check 1.x Value Generation Annotations
                    var generatedOnAddAnnotation = annotatable[OracleAnnotationNames.LegacyValueGeneratedOnAdd];
                    if (generatedOnAddAnnotation != null && (bool)generatedOnAddAnnotation)
                        valueGenerationStrategy = OracleValueGenerationStrategy.IdentityColumn;
                    var generatedOnAddOrUpdateAnnotation = annotatable[OracleAnnotationNames.LegacyValueGeneratedOnAddOrUpdate];
                    if (generatedOnAddOrUpdateAnnotation != null && (bool)generatedOnAddOrUpdateAnnotation)
                        valueGenerationStrategy = OracleValueGenerationStrategy.ComputedColumn;
                }
            }

            if ((valueGenerationStrategy == OracleValueGenerationStrategy.IdentityColumn) && string.IsNullOrWhiteSpace(defaultValueSql) && defaultValue == null)
            {
                switch (matchType)
                {
                    case "tinyint":
                    case "smallint":
                    case "mediumint":
                    case "int":
                    case "bigint":
                        autoIncrement = true;
                        break;
                    case "datetime":
                        if (!_options.ConnectionSettings.ServerVersion.SupportsDateTime6)
                            throw new InvalidOperationException(
                                $"Error in {table}.{name}: DATETIME does not support values generated " +
                                "on Add or Update in Oracle <= 5.5, try explicitly setting the column type to TIMESTAMP");
                        goto case "timestamp";
                    case "timestamp":
                        defaultValueSql = $"CURRENT_TIMESTAMP({matchLen})";
                        break;
                }
            }

            string onUpdateSql = null;
            if (valueGenerationStrategy == OracleValueGenerationStrategy.ComputedColumn)
            {
                switch (matchType)
                {
                    case "datetime":
                        if (!_options.ConnectionSettings.ServerVersion.SupportsDateTime6)
                        {
                            throw new InvalidOperationException($"Error in {table}.{name}: DATETIME does not support values generated " +
                                "on Add or Update in Oracle <= 5.5, try explicitly setting the column type to TIMESTAMP");
                        }

                        goto case "timestamp";
                    case "timestamp":
                        if (string.IsNullOrWhiteSpace(defaultValueSql) && defaultValue == null)
                        {
                            defaultValueSql = $"CURRENT_TIMESTAMP({matchLen})";
                        }

                        onUpdateSql = $"CURRENT_TIMESTAMP({matchLen})";
                        break;
                }
            }

            builder
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(name))
                .Append(" ")
                .Append(type ?? GetColumnType(schema, table, name, clrType, unicode, maxLength, rowVersion, model));

            if (!nullable)
            {
                builder.Append(" NOT NULL");
            }

            if (autoIncrement)
            {
                builder.Append(" AUTO_INCREMENT");
            }
            else
            {
                if (defaultValueSql != null)
                {
                    builder
                        .Append(" DEFAULT ")
                        .Append(defaultValueSql);
                }
                else if (defaultValue != null)
                {
                    var defaultValueLiteral = Dependencies.TypeMapper.GetMapping(clrType);
                    builder
                        .Append(" DEFAULT ")
                        .Append(defaultValueLiteral.GenerateSqlLiteral(defaultValue));
                }
                if (onUpdateSql != null)
                {
                    builder
                        .Append(" ON UPDATE ")
                        .Append(onUpdateSql);
                }
            }

        }

        protected override void DefaultValue(object defaultValue, string defaultValueSql, MigrationCommandListBuilder builder)
        {
            Check.NotNull(builder, nameof(builder));

            if (defaultValueSql != null)
            {
                builder
                    .Append(" DEFAULT ")
                    .Append(defaultValueSql);
            }
            else if (defaultValue != null)
            {
                var typeMapping = Dependencies.TypeMapper.GetMapping(defaultValue.GetType());
                builder
                    .Append(" DEFAULT ")
                    .Append(typeMapping.GenerateSqlLiteral(defaultValue));
            }
        }

        protected override void Generate([NotNull] DropForeignKeyOperation operation, [CanBeNull] IModel model, [NotNull] MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            builder
                .Append("ALTER TABLE ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
                .Append(" DROP FOREIGN KEY ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
                .AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);

            EndStatement(builder);
        }

        protected override void Generate([NotNull] AddPrimaryKeyOperation operation, [CanBeNull] IModel model, [NotNull] MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            builder
                .Append("ALTER TABLE ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
                .Append(" ADD ");
            PrimaryKeyConstraint(operation, model, builder);
            builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);

            var annotations = model.GetAnnotations();
            if (operation.Columns.Count() == 1)
            {
                builder.Append(@"DROP PROCEDURE IF EXISTS POMELO_AFTER_ADD_PRIMARY_KEY;
CREATE PROCEDURE POMELO_AFTER_ADD_PRIMARY_KEY(IN `SCHEMA_NAME_ARGUMENT` VARCHAR(255), IN `TABLE_NAME_ARGUMENT` VARCHAR(255), IN `COLUMN_NAME_ARGUMENT` VARCHAR(255))
BEGIN
	DECLARE HAS_AUTO_INCREMENT_ID INT(11);
	DECLARE PRIMARY_KEY_COLUMN_NAME VARCHAR(255);
	DECLARE PRIMARY_KEY_TYPE VARCHAR(255);
	DECLARE SQL_EXP VARCHAR(1000);

	SELECT COUNT(*)
		INTO HAS_AUTO_INCREMENT_ID
		FROM `information_schema`.`COLUMNS`
		WHERE `TABLE_SCHEMA` = (SELECT IFNULL(SCHEMA_NAME_ARGUMENT, SCHEMA()))
			AND `TABLE_NAME` = TABLE_NAME_ARGUMENT
			AND `COLUMN_NAME` = COLUMN_NAME_ARGUMENT
			AND `COLUMN_TYPE` LIKE '%int%'
			AND `COLUMN_KEY` = 'PRI';
	IF HAS_AUTO_INCREMENT_ID THEN
		SELECT `COLUMN_TYPE`
			INTO PRIMARY_KEY_TYPE
			FROM `information_schema`.`COLUMNS`
			WHERE `TABLE_SCHEMA` = (SELECT IFNULL(SCHEMA_NAME_ARGUMENT, SCHEMA()))
				AND `TABLE_NAME` = TABLE_NAME_ARGUMENT
				AND `COLUMN_NAME` = COLUMN_NAME_ARGUMENT
				AND `COLUMN_TYPE` LIKE '%int%'
				AND `COLUMN_KEY` = 'PRI';
		SELECT `COLUMN_NAME`
			INTO PRIMARY_KEY_COLUMN_NAME
			FROM `information_schema`.`COLUMNS`
			WHERE `TABLE_SCHEMA` = (SELECT IFNULL(SCHEMA_NAME_ARGUMENT, SCHEMA()))
				AND `TABLE_NAME` = TABLE_NAME_ARGUMENT
				AND `COLUMN_NAME` = COLUMN_NAME_ARGUMENT
				AND `COLUMN_TYPE` LIKE '%int%'
				AND `COLUMN_KEY` = 'PRI';
		SET SQL_EXP = CONCAT('ALTER TABLE `', (SELECT IFNULL(SCHEMA_NAME_ARGUMENT, SCHEMA())), '`.`', TABLE_NAME_ARGUMENT, '` MODIFY COLUMN `', PRIMARY_KEY_COLUMN_NAME, '` ', PRIMARY_KEY_TYPE, ' NOT NULL AUTO_INCREMENT;');
		SET @SQL_EXP = SQL_EXP;
		PREPARE SQL_EXP_EXECUTE FROM @SQL_EXP;
		EXECUTE SQL_EXP_EXECUTE;
		DEALLOCATE PREPARE SQL_EXP_EXECUTE;
	END IF;
END;");
                builder.AppendLine();

                if (operation.Schema == null)
                {
                    builder.Append($"CALL POMELO_AFTER_ADD_PRIMARY_KEY(NULL, '{ operation.Table }', '{ operation.Columns.First() }');");
                }
                else
                {
                    builder.Append($"CALL POMELO_AFTER_ADD_PRIMARY_KEY('{ operation.Schema }', '{ operation.Table }', '{ operation.Columns.First() }');");
                }
                builder.AppendLine();
                builder.Append($"DROP PROCEDURE IF EXISTS POMELO_AFTER_ADD_PRIMARY_KEY;");
                builder.AppendLine();
            }

            EndStatement(builder);
        }

        protected override void Generate([NotNull] DropPrimaryKeyOperation operation, [CanBeNull] IModel model, [NotNull] MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));
            builder.Append(@"DROP PROCEDURE IF EXISTS POMELO_BEFORE_DROP_PRIMARY_KEY;
CREATE PROCEDURE POMELO_BEFORE_DROP_PRIMARY_KEY(IN `SCHEMA_NAME_ARGUMENT` VARCHAR(255), IN `TABLE_NAME_ARGUMENT` VARCHAR(255))
BEGIN
	DECLARE HAS_AUTO_INCREMENT_ID TINYINT(1);
	DECLARE PRIMARY_KEY_COLUMN_NAME VARCHAR(255);
	DECLARE PRIMARY_KEY_TYPE VARCHAR(255);
	DECLARE SQL_EXP VARCHAR(1000);

	SELECT COUNT(*)
		INTO HAS_AUTO_INCREMENT_ID
		FROM `information_schema`.`COLUMNS`
		WHERE `TABLE_SCHEMA` = (SELECT IFNULL(SCHEMA_NAME_ARGUMENT, SCHEMA()))
			AND `TABLE_NAME` = TABLE_NAME_ARGUMENT
			AND `Extra` = 'auto_increment'
			AND `COLUMN_KEY` = 'PRI'
			LIMIT 1;
	IF HAS_AUTO_INCREMENT_ID THEN
		SELECT `COLUMN_TYPE`
			INTO PRIMARY_KEY_TYPE
			FROM `information_schema`.`COLUMNS`
			WHERE `TABLE_SCHEMA` = (SELECT IFNULL(SCHEMA_NAME_ARGUMENT, SCHEMA()))
				AND `TABLE_NAME` = TABLE_NAME_ARGUMENT
				AND `COLUMN_KEY` = 'PRI'
			LIMIT 1;
		SELECT `COLUMN_NAME`
			INTO PRIMARY_KEY_COLUMN_NAME
			FROM `information_schema`.`COLUMNS`
			WHERE `TABLE_SCHEMA` = (SELECT IFNULL(SCHEMA_NAME_ARGUMENT, SCHEMA()))
				AND `TABLE_NAME` = TABLE_NAME_ARGUMENT
				AND `COLUMN_KEY` = 'PRI'
			LIMIT 1;
		SET SQL_EXP = CONCAT('ALTER TABLE `', (SELECT IFNULL(SCHEMA_NAME_ARGUMENT, SCHEMA())), '`.`', TABLE_NAME_ARGUMENT, '` MODIFY COLUMN `', PRIMARY_KEY_COLUMN_NAME, '` ', PRIMARY_KEY_TYPE, ' NOT NULL;');
		SET @SQL_EXP = SQL_EXP;
		PREPARE SQL_EXP_EXECUTE FROM @SQL_EXP;
		EXECUTE SQL_EXP_EXECUTE;
		DEALLOCATE PREPARE SQL_EXP_EXECUTE;
	END IF;
END;");
            builder.AppendLine();

            if (string.IsNullOrWhiteSpace(operation.Schema))
            {
                builder.Append($"CALL POMELO_BEFORE_DROP_PRIMARY_KEY(NULL, '{ operation.Table }');");
            }
            else
            {
                builder.Append($"CALL POMELO_BEFORE_DROP_PRIMARY_KEY('{ operation.Schema }', '{ operation.Table }');");
            }
            builder.AppendLine();
            builder.Append($"DROP PROCEDURE IF EXISTS POMELO_BEFORE_DROP_PRIMARY_KEY;");
            builder.AppendLine();

            builder
                .Append("ALTER TABLE ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
                .Append(" DROP PRIMARY KEY;")
                .AppendLine();

            EndStatement(builder);
        }

        public virtual void Rename(
            [CanBeNull] string schema,
            [NotNull] string name,
            [NotNull] string newName,
            [NotNull] string type,
            [NotNull] MigrationCommandListBuilder builder)
        {
            Check.NotEmpty(name, nameof(name));
            Check.NotEmpty(newName, nameof(newName));
            Check.NotEmpty(type, nameof(type));
            Check.NotNull(builder, nameof(builder));


            builder
                .Append("ALTER ")
                .Append(type)
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(name, schema))
                .Append(" RENAME TO ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(newName, schema));
        }

        public virtual void Transfer(
            [NotNull] string newSchema,
            [CanBeNull] string schema,
            [NotNull] string name,
            [NotNull] string type,
            [NotNull] MigrationCommandListBuilder builder)
        {
            Check.NotEmpty(newSchema, nameof(newSchema));
            Check.NotEmpty(name, nameof(name));
            Check.NotNull(type, nameof(type));
            Check.NotNull(builder, nameof(builder));

            builder
                .Append("ALTER ")
                .Append(type)
                .Append(" ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(name, schema))
                .Append(" SET SCHEMA ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(newSchema));
        }

        protected override void ForeignKeyAction(ReferentialAction referentialAction, MigrationCommandListBuilder builder)
        {
            Check.NotNull(builder, nameof(builder));

            if (referentialAction == ReferentialAction.Restrict)
            {
                builder.Append("RESTRICT");
            }
            else
            {
                base.ForeignKeyAction(referentialAction, builder);
            }
        }

        protected override void ForeignKeyConstraint(
            [NotNull] AddForeignKeyOperation operation,
            [CanBeNull] IModel model,
            [NotNull] MigrationCommandListBuilder builder)
        {
            Check.NotNull(operation, nameof(operation));
            Check.NotNull(builder, nameof(builder));

            if (operation.Name != null)
            {
                builder
                    .Append("CONSTRAINT ")
                    .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name.Substring(0, Math.Min(operation.Name.Length, 64))))
                    .Append(" ");
            }

            builder
                .Append("FOREIGN KEY (")
                .Append(ColumnList(operation.Columns))
                .Append(") REFERENCES ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.PrincipalTable, operation.PrincipalSchema));

            if (operation.PrincipalColumns != null)
            {
                builder
                    .Append(" (")
                    .Append(ColumnList(operation.PrincipalColumns))
                    .Append(")");
            }

            if (operation.OnUpdate != ReferentialAction.NoAction)
            {
                builder.Append(" ON UPDATE ");
                ForeignKeyAction(operation.OnUpdate, builder);
            }

            if (operation.OnDelete != ReferentialAction.NoAction)
            {
                builder.Append(" ON DELETE ");
                ForeignKeyAction(operation.OnDelete, builder);
            }
        }

        protected override string ColumnList(string[] columns) => string.Join(", ", columns.Select(Dependencies.SqlGenerationHelper.DelimitIdentifier));
    }

    public static class StringExtensions
    {
        /// <summary>
        /// Method that limits the length of text to a defined length.
        /// </summary>
        /// <param name="source">The source text.</param>
        /// <param name="maxLength">The maximum limit of the string to return.</param>
        public static string LimitLength(this string source, int maxLength)
        {
            if (source.Length <= maxLength)
            {
                return source;
            }

            return source.Substring(0, maxLength);
        }
    }
}
