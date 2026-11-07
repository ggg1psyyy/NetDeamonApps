using LinqToDB;
using LinqToDB.Data;
using System.Globalization;
using LinqToDB.Mapping;
using Microsoft.Data.Sqlite;

namespace PVControl
{
    /// <summary>LinqToDB connection to the cost table.</summary>
    public partial class EnergyCostDb : DataConnection
    {
        /// <summary>Connection with the default configuration.</summary>
        public EnergyCostDb()
        {
            InitDataContext();
        }

        /// <summary>Connection with a named configuration.</summary>
        /// <param name="configuration">Configuration name.</param>
        public EnergyCostDb(string configuration)
            : base(configuration)
        {
            InitDataContext();
        }

        /// <summary>Connection with explicit options.</summary>
        /// <param name="options">Provider and connection string.</param>
        public EnergyCostDb(DataOptions options)
            : base(options)
        {
            InitDataContext();
        }

        partial void InitDataContext();

        /// <summary>
        /// The cost log's timestamps as text without trailing zero fractions ("2026-11-07 15:00:00"): linq2db 6 writes a
        /// <see cref="DateTime"/> with milliseconds ("… 15:00:00.000") where 5 and every row before 2026-11-07 15:00
        /// didn't — one format in the column for queries that match the text.
        /// </summary>
        public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFF";

        static readonly MappingSchema TimestampAsText = BuildTimestampAsText();

        static MappingSchema BuildTimestampAsText()
        {
            var ms = new MappingSchema();
            ms.SetConvertExpression<DateTime, DataParameter>(d =>
                new DataParameter { Value = d.ToString(TimestampFormat, CultureInfo.InvariantCulture), DataType = DataType.NVarChar });
            return ms;
        }

        partial void InitDataContext() => AddMappingSchema(TimestampAsText);

        /// <summary>The cost entries.</summary>
        public ITable<Costs> CostEntries  => this.GetTable<Costs>();
    }
}