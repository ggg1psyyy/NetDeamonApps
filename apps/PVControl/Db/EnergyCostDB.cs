using LinqToDB;
using LinqToDB.Data;
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

        /// <summary>The cost entries.</summary>
        public ITable<Costs> CostEntries  => this.GetTable<Costs>();
    }
}