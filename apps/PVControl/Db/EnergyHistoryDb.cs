// ---------------------------------------------------------------------------------------------------
// <auto-generated>
// This code was generated by LinqToDB scaffolding tool (https://github.com/linq2db/linq2db).
// Changes to this file may cause incorrect behavior and will be lost if the code is regenerated.
// </auto-generated>
// ---------------------------------------------------------------------------------------------------

using LinqToDB;
using LinqToDB.Data;

#pragma warning disable 1573, 1591
#nullable enable

namespace PVControl
{
	public partial class EnergyHistoryDb : DataConnection
	{
		public EnergyHistoryDb()
		{
			InitDataContext();
		}

		public EnergyHistoryDb(string configuration)
			: base(configuration)
		{
			InitDataContext();
		}

		public EnergyHistoryDb(DataOptions options)
			: base(options)
		{
			InitDataContext();
		}

		partial void InitDataContext();

		public ITable<Daily>  Dailies  => this.GetTable<Daily>();
		public ITable<Hourly> Hourlies => this.GetTable<Hourly>();
	}
}
