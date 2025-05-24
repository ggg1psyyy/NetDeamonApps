using LinqToDB.Mapping;
namespace PVControl
{
    [Table("costs")]
    public class Costs
    {
        [Column("timestamp")] public DateTime Timestamp { get; set; } // text(max)
        [Column("gridimporttotal")] public float? GridImportTotal { get; set; } // real
        [Column("gridexporttotal")] public float? GridExportTotal { get; set; } // real
        [Column("gridimport")] public float? GridImport { get; set; } // real
        [Column("gridexport")] public float? GridExport { get; set; } // real
        [Column("baseprice")] public float? BasePrice { get; set; } // real
        [Column("importpriceprovider")] public float? ImportPriceProvider { get; set; } // real
        [Column("importpricetotal")] public float? ImportPriceTotal { get; set; } // real
        [Column("exportpriceprovider")] public float? ExportPriceProvider { get; set; } // real
        [Column("exportpricetotal")] public float? ExportPriceTotal { get; set; } // real
    }
}