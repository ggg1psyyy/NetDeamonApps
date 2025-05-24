using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;

namespace PVControl
{
    public partial class EnergyCostDb : DataConnection
    {
        public EnergyCostDb()
        {
            InitDataContext();
        }

        public EnergyCostDb(string configuration)
            : base(configuration)
        {
            InitDataContext();
        }

        public EnergyCostDb(DataOptions options)
            : base(options)
        {
            InitDataContext();
        }

        partial void InitDataContext();

        public ITable<Costs?> CostEntries  => this.GetTable<Costs>();
    }
}