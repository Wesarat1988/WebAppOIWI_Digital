using System;
using System.Collections.Generic;

namespace WepAppOIWI_Digital.Services;

public sealed class SetupStateStore
{
    public sealed class WorkOrderEntry
    {
        public string WorkOrder { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string Line { get; set; } = string.Empty;
    }

    public sealed class StationInfo
    {
        public string Line { get; set; } = string.Empty;
        public string Section { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public string Station { get; set; } = string.Empty;
    }

    public sealed class PendingMoConfig
    {
        public string WorkOrder { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string Line { get; set; } = string.Empty;
        public string Section { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public string Station { get; set; } = string.Empty;
    }

    public bool IsWorkOrderSelectionLocked { get; set; }
    public string SelectedWorkOrderId { get; set; } = string.Empty;

    public string WorkOrder { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Line { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Station { get; set; } = string.Empty;

    public string EmpNo { get; set; } = string.Empty;
    public string Factory { get; set; } = "DET6";
    public string GetDataType { get; set; } = "0";
    public string MoType { get; set; } = "0";
    public string InputLine { get; set; } = string.Empty;

    public string WorkOrderFilter { get; set; } = string.Empty;
    public string SearchFilter { get; set; } = string.Empty;

    public DateTimeOffset? LastUpdatedAt { get; set; }
    public string LastUpdatedBy { get; set; } = string.Empty;

    public List<WorkOrderEntry> MoResults { get; } = new();
    public List<StationInfo> StationList { get; } = new();
    public PendingMoConfig Pending { get; } = new();
}
