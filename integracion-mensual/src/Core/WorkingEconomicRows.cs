using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SistemaMarcaciones.Monthly.Controlled;
using SistemaMarcaciones.Monthly.Excel.Editor;
using SistemaMarcaciones.Monthly.Export.Verified;
using SistemaMarcaciones.Monthly.Projects;

namespace SistemaMarcaciones.F13.Integrated.Native;

public sealed record TypedLateReview(string RangeId,string MatrixId,string Sheet,string[] Coordinates,decimal MinutesPerDate,string Reason)
{
    public const string Kind="F13_TYPED_MANUAL_LATENESS_REVIEW_V1";
    public bool LabourApplied=>false;
}
public sealed record WorkingLateDate(string PersonKey,string Name,string Sheet,string Coordinate,DateOnly Date,
    decimal Minutes,string Origin,string? ReviewId);
public sealed record WorkingEconomicRow(string PersonKey,string Name,WorkingLateDate[] Dates,decimal Minutes,
    EconomicSourceCell[] OriginalEconomicCells,bool Pending)
{
    public decimal? ProposedAmount=>null;
    public string EconomicState=>Pending?"IMPACTO ECONÓMICO POR DETERMINAR":"ORIGINAL CONSERVADO; SIN NUEVO CÁLCULO";
    public string DateLabel=>string.Join(", ",Dates.Select(d=>d.Date).Distinct().Order().Select(d=>d.ToString("dd/MM/yyyy")));
    public string OriginalAmount=>OriginalEconomicCells.LastOrDefault()?.Value??"No determinado";
}

/// <summary>Read projection of the same saved MonthlyWork; never an economic/labour result collection.</summary>
public sealed record WorkingEconomicRows(string MatrixId,int Revision,WorkingEconomicRow[] Rows,int RosterPeople,
    int UnknownPersonDates,int ExcludedPeople,string[] EconomicSheets,string SourceSHA256)
{
    public const string Contract="F13_DYNAMIC_WORKING_ECONOMIC_ROWS_V1";
    public const string Warning="REVISIÓN MANUAL NO ACREDITADA. Solo atrasos conocidos en la revisión. No calcula descuentos ni acredita ausencia de atrasos en fechas sin evidencia.";
    public bool LabourRecalculated=>false;
    public bool EconomicRecalculated=>false;
    sealed record Box(WorkingEconomicRows Value);
    static readonly ConditionalWeakTable<MonthlyWork,ConditionalWeakTable<WorkbookProfile,Box>> cache=new();
    public static WorkingEconomicRows For(MonthlyWork work,WorkbookProfile profile)=>cache.GetValue(work,_=>new()).GetValue(profile,p=>new(Build(work,p))).Value;
    static string Label(string? value)=>string.Join(" ",(value??"").Trim().ToUpperInvariant().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries));
    static bool PersonHeader(string? value)=>Label(value) is "NOMBRES" or "NOMBRES Y APELLIDOS" or "APELLIDOS Y NOMBRES" or "SERVIDOR" or "PERSONA";
    static bool DeclaredLatenessMinutes(MatrixSheet sheet,MatrixCell cell)
    {
        var header=sheet.Cells.Where(c=>c.Row<cell.Row&&PersonHeader(c.Value)).OrderBy(c=>c.Row).LastOrDefault();
        return header!=null&&Label(sheet.Cells.SingleOrDefault(c=>c.Row==header.Row&&c.Column==cell.Column)?.Value) is "MINUTOS DE ATRASOS" or "MINUTOS DE ATRASO";
    }
    static bool Boundary(string? value)=>Label(value) is {} v&&(v.StartsWith("SUBTOTAL",StringComparison.Ordinal)||v.StartsWith("TOTAL",StringComparison.Ordinal)||v.StartsWith("CUERPO DE BOMBEROS",StringComparison.Ordinal)||v.StartsWith("VALORES A DESCONTAR",StringComparison.Ordinal)||v.StartsWith("REPORTE DE",StringComparison.Ordinal)||v.StartsWith("REVISADO Y",StringComparison.Ordinal));
    sealed record MoneyRow(string Sheet,string Name,EconomicSourceCell[] Cells);
    static bool EconomicHeader(IEnumerable<MatrixCell> row)=>row.Any(c=>PersonHeader(c.Value))
        &&row.Count(c=>Label(c.Value) is "MINUTOS" or "MINUTOS DE ATRASOS" or "MINUTOS DE ATRASO")==1
        &&row.Count(c=>Label(c.Value) is "VALOR" or "VALOR A DESCONTAR")==1;
    static MoneyRow[] MoneyRows(WorkbookProfile profile)
    {
        var result=new List<MoneyRow>();
        foreach(var sheet in profile.Draft.Sheets)
        {
            int person=0,minutes=0,amount=0;
            foreach(var group in sheet.Cells.GroupBy(c=>c.Row).OrderBy(g=>g.Key))
            {
                if(group.Any(c=>Boundary(c.Value))){person=minutes=amount=0;}
                var header=group.SingleOrDefault(c=>PersonHeader(c.Value));
                if(header!=null)
                {
                    var m=group.Where(c=>Label(c.Value) is "MINUTOS" or "MINUTOS DE ATRASOS" or "MINUTOS DE ATRASO").ToArray();
                    var a=group.Where(c=>Label(c.Value) is "VALOR" or "VALOR A DESCONTAR").ToArray();
                    person=header.Column;minutes=m.Length==1?m[0].Column:0;amount=a.Length==1?a[0].Column:0;continue;
                }
                var name=group.SingleOrDefault(c=>c.Column==person)?.Value;
                if(minutes>0&&amount>0&&!string.IsNullOrWhiteSpace(name))result.Add(new(sheet.Name,name,new[]{minutes,amount}.Select(col=>group.Single(c=>c.Column==col)).Select(c=>new EconomicSourceCell(sheet.Name,c.Coordinate,c.Value,c.Formula,c.Style)).ToArray()));
            }
        }
        return result.ToArray();
    }
    public static WorkingEconomicRows Build(MonthlyWork work,WorkbookProfile profile)
    {
        var draft=profile.Draft;
        if(CanonicalMonthly.Hash(MatrixReview.Definition(work,draft.Id))!=CanonicalMonthly.Hash(draft))throw new InvalidDataException("WORKING_ROWS_SOURCE_DEFINITION_CHANGED");
        var money=MoneyRows(profile);var pending=CrossMatrixReview.For(work).Matrix(draft.Id);
        var active=ManualRangeReview.Active(work,draft.Id);
        var current=active.SelectMany(r=>r.Cells).GroupBy(c=>MatrixReview.Key(c.Sheet,c.Coordinate)).ToDictionary(g=>g.Key,g=>g.Last(),StringComparer.Ordinal);
        var typed=ProjectEvents.Read<TypedLateReview>(work,TypedLateReview.Kind).Where(t=>t.MatrixId==draft.Id).ToArray();
        if(typed.Select(t=>t.RangeId).Distinct(StringComparer.Ordinal).Count()!=typed.Length)throw new InvalidDataException("DUPLICATE_TYPED_LATENESS_REVIEW");
        foreach(var t in typed)
        {
            if(t.MinutesPerDate<=0||t.MinutesPerDate>1440||decimal.Truncate(t.MinutesPerDate)!=t.MinutesPerDate||string.IsNullOrWhiteSpace(t.Reason))throw new InvalidDataException("INVALID_TYPED_LATENESS_MINUTES");
            var range=ManualRangeReview.Events(work,draft.Id).SingleOrDefault(r=>r.Id==t.RangeId)??throw new InvalidDataException("TYPED_LATENESS_RANGE_MISSING");
            if(range.Action!=EditAction.APPLY||range.Cells.Any(c=>c.Sheet!=t.Sheet||c.ChoiceId!="CUSTOM"||c.After!=t.MinutesPerDate.ToString(CultureInfo.InvariantCulture))
                ||!range.Cells.Select(c=>c.Coordinate).Order(StringComparer.Ordinal).SequenceEqual(t.Coordinates.Order(StringComparer.Ordinal)))throw new InvalidDataException("TYPED_LATENESS_RANGE_WITNESS_CHANGED");
        }
        VerifiedFact[] facts=[];bool native=false;
        var registration=ProjectEvents.Read<NativeMatrixRegistration>(work,NativeAttendanceCoordinator.Contract).SingleOrDefault(r=>r.MatrixId==draft.Id);
        if(registration!=null)
        {
            NativeAttendanceCoordinator.Verify(registration);
            using var document=JsonDocument.Parse(File.ReadAllText(registration.NativeSidecar));
            var bundle=document.RootElement.GetProperty("Bundle").Deserialize<NativeAttendanceBundle>(SistemaMarcaciones.Monthly.Native.Outcomes.B07Contract.Wire)??throw new InvalidDataException("NATIVE_WORKING_ROWS_BUNDLE_MISSING");
            if(bundle.ProjectId!=work.SessionId||bundle.Period!=draft.Period||CanonicalMonthly.Hash(bundle)!=registration.SourceSetSHA256)throw new InvalidDataException("NATIVE_WORKING_ROWS_BUNDLE_CHANGED");
            facts=bundle.Facts;native=true;
        }
        var dates=new List<WorkingLateDate>();var people=new Dictionary<string,string>(StringComparer.Ordinal);int unknown=0;
        foreach(var sheet in draft.Sheets)
        {
            var index=WorksheetInteractionIndex.For(profile,sheet.Name);
            foreach(var row in index.Targets.Values.GroupBy(t=>index.Cells[t.Coordinate].Row))
            {
                var header=sheet.Cells.Where(c=>c.Row<row.Key&&PersonHeader(c.Value)).OrderBy(c=>c.Row).LastOrDefault()??throw new InvalidDataException("WORKING_ROWS_PERSON_HEADER_MISSING");
                var name=sheet.Cells.SingleOrDefault(c=>c.Row==row.Key&&c.Column==header.Column)?.Value??throw new InvalidDataException("WORKING_ROWS_PERSON_NAME_MISSING");
                var ids=row.Select(t=>draft.Bindings.GetValueOrDefault(MatrixReview.Key(sheet.Name,t.Coordinate))?.PersonId).Where(p=>p!=null).Distinct(StringComparer.Ordinal).ToArray();
                if(ids.Length>1)throw new InvalidDataException("WORKING_ROWS_CONFLICTING_PERSON_BINDINGS");
                var key=ids.SingleOrDefault()??CanonicalMonthly.Hash(new{draft.SourceSHA256,Sheet=sheet.Name,Row=row.Key,Name=name});
                if(people.TryGetValue(key,out var priorName)&&priorName!=name)throw new InvalidDataException("WORKING_ROWS_PERSON_NAME_CONFLICT");people[key]=name;
                foreach(var target in row)
                {
                    var cell=index.Cells[target.Coordinate];var cellKey=MatrixReview.Key(sheet.Name,target.Coordinate);
                    current.TryGetValue(cellKey,out var edit);
                    var typedEdit=edit==null?null:typed.SingleOrDefault(t=>active.Any(a=>a.Id==t.RangeId&&a.Cells.Any(c=>c.Id==edit.Id)));
                    foreach(var date in target.Dates)
                    {
                        decimal? minutes=null;string origin="UNKNOWN";
                        if(typedEdit!=null){minutes=typedEdit.MinutesPerDate;origin=TypedLateReview.Kind;}
                        else if(edit!=null&&edit.ChoiceId is "PERSONAL" or "MEDICAL" or "VACATION" or "COMMISSION" or "LEAVE" or "OTHER_PERMISSION" or "ZERO")
                        {minutes=0;origin="EXPLICIT_MANUAL_NON_LATENESS_REVIEW_NOT_LABOUR_RESULT";}
                        else if(native&&ids.Length==1)
                        {
                            var unit=facts.Where(f=>f.PersonId==ids[0]&&f.Date==date).ToArray();
                            if(unit.Length>0){var late=unit.Where(f=>f.Category=="atraso").ToArray();minutes=late.Any(f=>f.Minutes==null)?null:late.Sum(f=>f.Minutes!.Value);origin="PINNED_NATIVE_FACTS_NOT_VISIBLE_TEXT";}
                        }
                        else if(target.Dates.Length==1&&DeclaredLatenessMinutes(sheet,cell)&&cell.Formula==null&&cell.Type is null or "n"&&decimal.TryParse(cell.Value,NumberStyles.Number,CultureInfo.InvariantCulture,out var numeric)&&numeric>=0)
                        {minutes=numeric;origin="EXACT_PHYSICAL_NUMERIC_SOURCE_MINUTES_NOT_ACCREDITATION";}
                        if(minutes==null){unknown++;continue;}
                        if(minutes>0)dates.Add(new(key,name,sheet.Name,target.Coordinate,date,minutes.Value,origin,edit?.Id));
                    }
                }
            }
        }
        if(dates.GroupBy(d=>(d.PersonKey,d.Date)).Any(g=>g.Count()>1))throw new InvalidDataException("WORKING_ROWS_DUPLICATE_PERSON_DATE_NOT_SUMMED");
        var rows=dates.GroupBy(d=>d.PersonKey,StringComparer.Ordinal).OrderBy(g=>g.Key,StringComparer.Ordinal).Select(g=>
        {
            var name=people[g.Key];var originals=money.Where(m=>m.Name==name).ToArray();
            if(originals.Length>1)throw new InvalidDataException("WORKING_ROWS_AMBIGUOUS_ECONOMIC_SOURCE");
            return new WorkingEconomicRow(g.Key,name,g.OrderBy(d=>d.Date).ToArray(),g.Sum(d=>d.Minutes),originals.SingleOrDefault()?.Cells??[],pending.Any(p=>p.PersonWitness==g.Key)||g.Any(d=>d.Origin==TypedLateReview.Kind));
        }).ToArray();
        return new(draft.Id,work.Revision,rows,people.Count,unknown,people.Count-rows.Length,draft.Sheets.Where(s=>s.Cells.GroupBy(c=>c.Row).Any(EconomicHeader)).Select(s=>s.Name).ToArray(),draft.SourceSHA256);
    }
}

public static class TypedLateCommands
{
    public static MonthlyWork Apply(MonthlyWork work,WorkbookProfile profile,string sheet,string[] coordinates,decimal minutes,bool combine,string reason,string actor,DateTimeOffset at)
    {
        if(minutes<=0||minutes>1440||decimal.Truncate(minutes)!=minutes)throw new InvalidDataException("ENTER_WHOLE_LATENESS_MINUTES_1_TO_1440");
        HistoricalFinalMatrices.RequireWorking(profile.Draft);
        var next=ManualRangeReview.Apply(work,profile,sheet,coordinates,"CUSTOM",minutes.ToString(CultureInfo.InvariantCulture),combine,reason,actor,at);
        var range=ManualRangeReview.Events(next,profile.Draft.Id).Last();
        var old=ProjectEvents.Read<TypedLateReview>(next,TypedLateReview.Kind).SingleOrDefault(t=>t.RangeId==range.Id);
        if(old!=null){if(old.MinutesPerDate!=minutes)throw new InvalidDataException("TYPED_MINUTES_CONFLICT");return next;}
        return ProjectEvents.Append(next,TypedLateReview.Kind,new TypedLateReview(range.Id,profile.Draft.Id,sheet,range.Cells.Select(c=>c.Coordinate).ToArray(),minutes,reason),actor,at);
    }
    public static async Task Save(ProjectCoordinator coordinator,ProjectHandle h,WorkbookProfile profile,int expected,string sheet,string[] coordinates,
        decimal minutes,bool combine,string reason,string actor,DateTimeOffset at,CancellationToken ct=default)
    {
        using var telemetry=EditorSaveProfile.Begin(expected,"TYPED_MANUAL_LATENESS");
        using(EditorSaveProfile.Stage("EDITOR_LOCK_WAIT"))await h.Serial.WaitAsync(ct);
        try
        {
            ProjectCoordinator.RequireWritable(h);var before=h.Work;if(before.Revision!=expected)throw new InvalidOperationException("CAS_MANUAL_REQUIRES_COMPARISON");
            var next=Apply(before,profile,sheet,coordinates,minutes,combine,reason,actor,at);
            if(ReferenceEquals(before,next))return;
            await h.Repository.Save(next,before.Revision,ct);
        }finally{h.Serial.Release();}
        // Refresh performs the confirmed read and post-commit notification through the accepted coordinator.
        // Commands do not use the visual snapshot as a write authority.
        await coordinator.Refresh(h,CancellationToken.None);telemetry.Confirm(h.Work.Revision);
    }
}
