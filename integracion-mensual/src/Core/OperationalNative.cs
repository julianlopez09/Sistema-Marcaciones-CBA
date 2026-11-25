using System.Globalization;
using System.Text.Json;
using SistemaMarcaciones.Infrastructure.Referencia;
using SistemaMarcaciones.Monthly.Controlled;
using SistemaMarcaciones.Monthly.Excel.Editor;
using SistemaMarcaciones.Monthly.Projects;

namespace SistemaMarcaciones.F13.Operational;

/// <summary>Transports imported evidence to the pinned F4 and F5 public entry points.</summary>
public static class OperationalNative
{
    public const string Contract="F13_OPERATIONAL_NATIVE_TRANSPORT_V1";
    const string InfrastructureSHA="8ED7FC6C0766A47EAE99D33E5B6AB019FC9B2EDA26FACA5EE02942AD5D00458E";
    static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value);
    static string Iso(DateTime value)=>value.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFF",CultureInfo.InvariantCulture).TrimEnd('.');
    static string Text(JsonElement value,string key,string fallback="")=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(key,out var found)&&found.ValueKind==JsonValueKind.String?found.GetString()??fallback:fallback;
    static object Proof(object value,string hash,int row,string source,string sheet="")=>new
    {
        Original=value,Normalizado=value,HashNormalizado=Phase4Candidate.HashNormalizado(Json(value)),
        Metadata=new{Archivo=source,Fuente=source,Fila=row,Hoja=sheet},
        Verificado=true,Computable=true,Revisable=false,Precision="Minuto",
        Origen=source,Criterio="EXACT_IMPORTED_RECORD_AND_EXPLICIT_SOURCE_REVIEW",PhysicalSourceSHA256=hash
    };
    public static async Task<MonthlyRun> Process(OperationalSession session,CancellationToken ct=default)
    {
        if(PhysicalMatrix.Hash(typeof(Phase4Candidate).Assembly.Location)!=InfrastructureSHA
            ||Phase4Candidate.ContractVersion!="CBA-FASE4-20261002-v1"||Phase5Candidate.ContractVersion!="CBA-FASE5-20261002-v1")
            throw new InvalidDataException("La identidad del motor admitido cambió.");
        var before=await session.SourceSet(ct);
        var coverage=await OperationalCoverage.Validate(session,ct);
        var reviewed=coverage!=null;
        var reviewHash=CanonicalMonthly.Hash(session.Review);
        var prior=session.LastRun;
        if(prior!=null&&session.MatchesProfile(prior)&&prior.SourceSetSHA256==before&&prior.ReviewSHA256==reviewHash
            &&(reviewed||prior.Days.All(d=>d.State!="COMPLETED")))return prior;
        var docs=await session.Documents(ct);
        var marks=session.Host.Marks;
        var people=session.People.ToDictionary(p=>p.Id,StringComparer.Ordinal);
        var duties=session.Duties;
        var previousDays=prior!=null&&session.MatchesProfile(prior)?prior.Days.ToDictionary(d=>(d.PersonId,d.Date)):[];
        var holidays=ProjectEvents.Read<JsonElement>(session.Handle!.Work,"CBA_HOLIDAYS_IMPORTED_PENDING_APPLICATION_V1")
            .SelectMany(h=>h.GetProperty("Entries").EnumerateArray()).Select(e=>DateOnly.ParseExact(e.GetProperty("Date").GetString()!,"yyyy-MM-dd",CultureInfo.InvariantCulture)).ToHashSet();
        var results=new List<OperationalDay>();
        foreach(var duty in duties.OrderBy(d=>d.Date).ThenBy(d=>d.PersonId,StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var person=people[duty.PersonId];
            if(person.Population=="GUARDIA"&&!session.IsSyntheticQa)
            {results.Add(new(person.Id,duty.Date,person.Population,"PENDING_EVIDENCE",null,[],"",Json(new{}),Json(new{Contract,EngineInvoked=false,Reason="INSTITUTIONAL_GUARD_EFFECTIVE_CONTEXT_NOT_ADMITTED"}),["Guardia real requiere calendario, nómina, servicio efectivo y contexto D/D+1 acreditados. El archivo de jornadas no basta por sí solo. Los siete B06 siguen bloqueados."]));continue;}
            if(person.Population=="GUARDIA"&&duties.Any(other=>other.PersonId==person.Id&&other.Date!=duty.Date&&Math.Abs(other.Date.DayNumber-duty.Date.DayNumber)<=1))
            {results.Add(new(person.Id,duty.Date,person.Population,"PENDING_EVIDENCE",null,[],"",Json(new{}),Json(new{Contract,Reason="AMBIGUOUS_ADJACENT_DUTY"}),["Las jornadas contiguas requieren pertenencia exacta de las marcaciones y prueba del servicio efectivo."]));continue;}
            var holiday=holidays.Contains(duty.Date);
            if(holiday){results.Add(new(person.Id,duty.Date,person.Population,"PENDING_EVIDENCE",null,[],"",Json(new{}),Json(new{Contract,Reason="HOLIDAY_APPLICATION_CONTRACT_PENDING"}),["El calendario de feriados requiere su aplicación acreditada; no se genera ausencia ni cero por inferencia."]));continue;}
            var previous=previousDays.GetValueOrDefault((person.Id,duty.Date));
            results.Add(await Task.Run(()=>Evaluate(person,duty,marks,docs,reviewed,session.PeriodFor(person.Population),coverage,previous),ct));
        }
        ct.ThrowIfCancellationRequested();
        if(before!=await session.SourceSet(ct))throw new InvalidOperationException("Las fuentes cambiaron durante el procesamiento. Se conservaron los resultados anteriores.");
        var run=new MonthlyRun(CanonicalMonthly.Hash(new{Contract,SourceSet=before,ReviewSHA256=reviewHash,session.EvaluationProfile}),before,reviewHash,DateTimeOffset.UtcNow,results.ToArray(),session.EvaluationProfile);
        await session.Event(OperationalSession.RunEvent,run,ct);
        return run;
    }
    static OperationalDay Evaluate(PersonnelRow person,DutyRow duty,JsonElement[] importedMarks,
        DocumentRevision[] documents,bool reviewed,MonthlyPeriod period,OperationalCoverageReceipt? coverage,OperationalDay? previous)
    {
        OperationalDay Pending(string reason,JsonElement calculation=default)=>new(person.Id,duty.Date,person.Population,
            "PENDING_EVIDENCE",null,[],"",calculation.ValueKind==JsonValueKind.Undefined?Json(new{}):calculation,
            Json(new{Contract,State="PENDING_EVIDENCE",Reason=reason}),[reason]);
        if(!reviewed)return Pending("Falta revisar y documentar el alcance de las fuentes de este trabajo.");
        if(coverage==null||!coverage.Coverage.Any(a=>SistemaMarcaciones.Monthly.ForwardAdmission.SourceCoverageContract.Covers(a,person.Id,duty.Date,"BIOMETRIC"))
            ||!coverage.Coverage.Any(a=>SistemaMarcaciones.Monthly.ForwardAdmission.SourceCoverageContract.Covers(a,person.Id,duty.Date,"DOCUMENT")))
            return Pending("No hay cobertura exacta admitida para esta persona y fecha.");
        if(duty.State!="CONFIRMADA")return Pending("La jornada no tiene una confirmación documental expresa.");
        if(duty.Kind=="DESCANSO")return Pending("El descanso requiere la acreditación de la guardia efectiva precedente.");
        if(duty.Kind!=person.Population)return Pending("La jornada no coincide con la población del servidor.");
        if(person.Population is "ADMIN" or "OPADMIN" && (TimeOnly.FromDateTime(duty.Start)!=new TimeOnly(8,0)
            ||TimeOnly.FromDateTime(duty.End)!=new TimeOnly(16,30)||DateOnly.FromDateTime(duty.End)!=duty.Date))
            return Pending("El horario importado no corresponde al horario admitido de 08h00 a 16h30.");
        var guard=person.Population=="GUARDIA";
        var relevant=documents.Where(d=>d.Data.PersonId==person.Id
            &&(d.Data.StartDate==null||d.Data.EndDate==null||d.Data.StartDate<=duty.Date.AddDays(guard?1:0)&&d.Data.EndDate>=duty.Date)).ToArray();
        if(relevant.Any(d=>d.Data.Missing().Length>0||d.Data.StartDate==null||d.Data.EndDate==null))
            return Pending("Hay documentos potencialmente relacionados con información incompleta.");
        if(relevant.Any(d=>d.Data.ApprovalStatus=="DESCONOCIDO"||d.Data.DocumentValidityStatus=="DESCONOCIDO"||d.Data.IntervalCoverageStatus=="DESCONOCIDA"))
            return Pending("El estado o el alcance de un documento todavía es desconocido.");
        if(relevant.Any(d=>d.Data.Module==DocumentModule.VACATION&&(d.Data.ExactReturn!=null||d.Data.ReturnDate!=null)))
            return Pending("La reincorporación exacta de vacaciones requiere el adaptador de vigencia admitido; no se ajustan días por inferencia.");
        if(relevant.Any(d=>d.Data.Module is DocumentModule.ECU911 or DocumentModule.GUARD_CHANGE or DocumentModule.MANUAL_ATTENDANCE or DocumentModule.JUSTIFICATION))
            return Pending("La jornada contiene un respaldo que requiere su contrato específico de aplicación.");
        var records=new Dictionary<string,List<object>>(StringComparer.Ordinal);
        var proofs=new Dictionary<string,object>(StringComparer.Ordinal);
        foreach(var doc in relevant)
        {
            var d=doc.Data;
            var entity=d.Module switch
            {
                DocumentModule.COMMISSION=>"comisiones",DocumentModule.VACATION=>"vacaciones",
                DocumentModule.LEAVE=>"licencias",DocumentModule.CFE=>"cursos",_=>"permisos"
            };
            var idKey=entity switch{"permisos"=>"PermisoID","comisiones"=>"ComisionID","vacaciones"=>"VacationID","licencias"=>"LicenciaID",_=>"CursoID"};
            var full=d.IntervalCoverageStatus is "DIA_COMPLETO" or "JORNADA_COMPLETA" or "FULL_DAY";
            if(!full&&(d.Start==null||d.End==null))return Pending("El documento carece de horas o de una cobertura de día completo acreditada.");
            var payload=new Dictionary<string,object?>
            {
                [idKey]=doc.RecordId,["ServidorID"]=person.Id,["FechaInicio"]=d.StartDate!.Value.ToString("yyyy-MM-dd"),
                ["FechaFin"]=d.EndDate!.Value.ToString("yyyy-MM-dd"),["JornadaCompleta"]=full,
                ["Estado"]=d.ApprovalStatus,["EstadoAprobacion"]=d.ApprovalStatus,["EstadoVigencia"]=d.DocumentValidityStatus,["EstadoEntrega"]=d.PhysicalDeliveryStatus,
                ["Anulado"]=d.DocumentValidityStatus is "ANULADO" or "NULO" or "RECHAZADO",
                ["NumeroDocumento"]=d.OriginalNumber,["TipoPermiso"]=d.Module==DocumentModule.MEDICAL_PERMISSION?"MEDICO":"PERSONAL",
                ["HoraDesde"]=d.Start?.ToString("HH:mm:ss"),["HoraHasta"]=d.End?.ToString("HH:mm:ss"),
                ["HoraSalida"]=d.Start?.ToString("HH:mm:ss"),["HoraRetorno"]=d.End?.ToString("HH:mm:ss"),
                ["Motivo"]=d.Description??d.Module.ToString(),["Rol"]=d.Role,["RolActividad"]=d.Role,["TipoLicencia"]=d.Role,["Modalidad"]=d.Modality,
                ["FuenteArchivo"]=d.Origin.SourceFileName,["FilaFuente"]=d.Origin.RowOrRecordLocator
            };
            if(!records.TryGetValue(entity,out var group)){group=[];records.Add(entity,group);}
            group.Add(new{record_id=doc.RecordId,payload});
            var locator=System.Text.RegularExpressions.Regex.Match(d.Origin.RowOrRecordLocator,@"(?:row:|fila:)?([1-9][0-9]*)$");
            if(!locator.Success)return Pending("El documento requiere un localizador de origen caracterizado.");
            proofs.Add(doc.RecordId,Proof(payload,d.Origin.SourceSHA256,int.Parse(locator.Groups[1].Value,CultureInfo.InvariantCulture),d.Origin.SourceFileName,d.Origin.SheetOrPage));
        }
        var dayMarks=importedMarks.Where(m=>Text(m,"ServidorID")==person.Id).Select(m=>new{
            Id=Text(m,"MarcacionID"),At=OperationalCsv.Date(Text(m,"Fecha")).ToDateTime(TimeOnly.Parse(Text(m,"HoraOriginal"),CultureInfo.InvariantCulture)),Raw=m
        }).Where(m=>guard?DateOnly.FromDateTime(m.At)==duty.Date||DateOnly.FromDateTime(m.At)==duty.Date.AddDays(1):DateOnly.FromDateTime(m.At)==duty.Date).OrderBy(m=>m.At).ToArray();
        var journey=person.Population switch{"OPADMIN"=>"OPERATIVO_ADMINISTRATIVO","DIRECTORES"=>"DIRECTOR","GUARDIA"=>"OPERATIVO_GUARDIA",_=>"ADMINISTRATIVO"};
        var payloadInput=new Dictionary<string,object?>
        {
            ["Journey"]=journey,["Day"]=duty.Date.ToString("yyyy-MM-dd"),["Period"]=period.Start.ToString("yyyy-MM"),
            ["Identity"]=new{Employee=person.Identity,Name=person.Name,ByIdentity=new Dictionary<string,string[]>{{person.Identity,[person.Id]}},ByName=new Dictionary<string,string[]>(),ByAlias=new Dictionary<string,string[]>()},
            ["ServerPayload"]=new{ServidorID=person.Id,NombresApellidos=person.Name,EsDirector=person.Population=="DIRECTORES",OperativoLaboresAdministrativas=person.Population=="OPADMIN",Operativo=person.Population is "OPADMIN" or "GUARDIA"},
            ["Records"]=records.ToDictionary(p=>p.Key,p=>p.Value.ToArray()),["DocumentProofs"]=proofs,
            ["EventOrigins"]=new Dictionary<string,string>(),["VersionReglas"]="CONTINUIDAD-20261001",
            ["CoverageSource"]=new{State="COBERTURA_COMPLETA",Source=duty.Reference,Days=new[]{duty.Date.ToString("yyyy-MM-dd")}},
            ["Marks"]=dayMarks.Select(m=>new{At=Iso(m.At),MarcacionID=m.Id,Fuente=Text(m.Raw,"FuenteBiometrica"),SourceSHA256=Text(m.Raw,"FuenteHash")}).ToArray()
        };
        if(guard)
        {
            if(!coverage.Coverage.Any(a=>SistemaMarcaciones.Monthly.ForwardAdmission.SourceCoverageContract.Covers(a,person.Id,duty.Date.AddDays(1),"BIOMETRIC")))
                return Pending("Falta cobertura biométrica acreditada de D+1.");
            if(duty.Start!=duty.Date.ToDateTime(new(7,0))||duty.End!=duty.Date.AddDays(1).ToDateTime(new(7,0))||duty.Platoon.Length==0)
                return Pending("La guardia requiere intervalo 07h00 a 07h00 y pelotón acreditados.");
            var id="GUARD-"+person.Id+"-"+duty.Date.ToString("yyyyMMdd");
            var calendar=new{Fecha=duty.Date.ToString("yyyy-MM-dd"),Inicio=Iso(duty.Start),Fin=Iso(duty.End),PelotonID=duty.Platoon,CalendarioOficial=true};
            var assignment=new{AsignacionID=duty.Reference,GuardiaID=id,ServidorID=person.Id,PelotonID=duty.Platoon,Tipo="NOMINA_CONFIRMADA",Estado="CONFIRMADA",Inicio=Iso(duty.Start),Fin=Iso(duty.End)};
            payloadInput["GuardiaID"]=id;payloadInput["DiaEvaluacion"]=duty.Date.ToString("yyyy-MM-dd");
            payloadInput["Calendar"]=calendar;payloadInput["Assignment"]=assignment;
            payloadInput["CalendarProof"]=Proof(calendar,duty.SourceSHA256,duty.SourceRow,"JORNADAS_IMPORTADAS");
            payloadInput["AssignmentProof"]=Proof(assignment,duty.SourceSHA256,duty.SourceRow,"JORNADAS_IMPORTADAS");
            payloadInput["CoverageD"]=new{Fecha=duty.Date.ToString("yyyy-MM-dd"),Estado="COBERTURA_COMPLETA",Fuente=duty.Reference};
            payloadInput["CoverageD1"]=new{Fecha=duty.Date.AddDays(1).ToString("yyyy-MM-dd"),Estado="COBERTURA_COMPLETA",Fuente=duty.Reference};
        }
        var input=Json(payloadInput);var inputHash=CanonicalMonthly.Hash(input);
        var started=DateTimeOffset.UtcNow;
        JsonElement output;
        var reused=previous?.State=="COMPLETED"&&previous.InputSHA256==inputHash;
        try{output=reused?previous!.Calculation:guard?Phase5Candidate.Evaluate("guardia_normalizada",input):Phase4Candidate.Evaluate("consumidor",input);}
        catch(NotSupportedException error){return Pending(error.Message);}
        if(Text(output,"EstadoOrquestacion")!="EVALUADO_SHADOW")return Pending(Text(output,"Motivo","El motor dejó la jornada pendiente de revisión."),output);
        if(!output.TryGetProperty("ExplicacionDecision",out var decision))return Pending("El motor no aportó la explicación completa de la decisión.",output);
        if(guard)
        {
            foreach(var key in new[]{"MarksUsed","MarksIgnored","DocumentosAplicables","RequiereRevision","Reglas"})
                if(!decision.TryGetProperty(key,out _))return Pending("La traza de Guardia no contiene "+key+".",output);
            decision=Json(new{MarksUsed=decision.GetProperty("MarksUsed"),MarksIgnored=decision.GetProperty("MarksIgnored"),
                DocumentsApplied=decision.GetProperty("DocumentosAplicables"),Review=decision.GetProperty("RequiereRevision"),
                Rules=decision.GetProperty("Reglas"),CoveredIntervals=output.GetProperty("CoberturasAplicadas"),Provenance=output.GetProperty("Procedencia"),
                OriginalFrozenDecision=decision,Adapter="F13_F5_DECISION_FIELD_TRANSPORT_V1"});
        }
        foreach(var key in new[]{"MarksUsed","MarksIgnored","DocumentsApplied","CoveredIntervals","Rules","Provenance","Review"})
            if(!decision.TryGetProperty(key,out _))return Pending("La traza del motor está incompleta. Falta "+key+".",output);
        var applied=decision.GetProperty("DocumentsApplied").EnumerateArray().Select(v=>v.GetString()!).ToArray();
        if(applied.Any(id=>!proofs.ContainsKey(id)))throw new InvalidDataException("El motor aplicó un documento ajeno a la entrada.");
        var raw=output.GetProperty("Resultados").EnumerateArray().ToArray();
        if(raw.Length==0&&decision.GetProperty("Review").GetBoolean())return Pending("La jornada mantiene una revisión sin resolver.",output);
        var cases=raw.Select((r,index)=>new OperationalCase(inputHash+":"+index,person.Id,duty.Date,
            Text(r,"ReglaId"),Text(r,"Estado"),r.TryGetProperty("Minutos",out var minutes)&&minutes.ValueKind==JsonValueKind.Number?minutes.GetDecimal():null,
            dayMarks.Select(m=>m.At.ToString("HH:mm:ss")).ToArray(),applied,Text(r,"Observacion",OperationalLabels.Case(Text(r,"ReglaId"))))).ToArray();
        if(person.Population=="ADMIN"&&cases.Any(c=>c.Type=="MARCACIONES_ADICIONALES"))
            return Pending("Las marcaciones adicionales requieren comprobar el régimen de excepción aplicable a la identidad exacta. No se genera un requerimiento automático.",output);
        var trace=Json(new{
            Contract="F13_OPERATIONAL_CAPTURED_ENGINE_DECISION_V1",Transport=Contract,InputSHA256=inputHash,
            EngineInvoked=!reused,Invocation=reused?"REUSED_EXACT_NATIVE_INPUT":"NATIVE_PUBLIC_KERNEL_EXECUTED",
            ReusedCalculationSHA256=reused?CanonicalMonthly.Hash(previous!.Calculation):null,
            StartedUTC=reused?previous!.Trace.GetProperty("StartedUTC").GetDateTimeOffset():started,
            FinishedUTC=reused?previous!.Trace.GetProperty("FinishedUTC").GetDateTimeOffset():DateTimeOffset.UtcNow,CapturedAtUTC=DateTimeOffset.UtcNow,
            Engine=guard?Phase5Candidate.ContractVersion:Phase4Candidate.ContractVersion,ImplementationSHA256=InfrastructureSHA,
            person.Id,duty.Date,TemporalClassification=person.Population,EffectiveSchedule=duty,
            MarksEvaluated=dayMarks.Select(m=>m.Raw).ToArray(),Decision=decision,DocumentsExamined=relevant,
            Sources=dayMarks.Select(m=>Text(m.Raw,"FuenteHash")).Append(person.SourceSHA256).Append(duty.SourceSHA256).Concat(relevant.Select(d=>d.Data.Origin.SourceSHA256)).Distinct().ToArray(),
            SourceCoverage=coverage,
            InstitutionalCertification=false,EconomicRecalculation=false
        });
        string value=OperationalPresentation.Value(cases,raw);
        var fullDocuments=relevant.Where(d=>applied.Contains(d.RecordId,StringComparer.Ordinal)
            &&d.Data.IntervalCoverageStatus is "DIA_COMPLETO" or "JORNADA_COMPLETA" or "FULL_DAY").ToArray();
        if(fullDocuments.Length==1&&cases.Length==0)value=fullDocuments[0].Data.Module switch
        {DocumentModule.PERSONAL_PERMISSION=>"PERMISO PERSONAL",DocumentModule.MEDICAL_PERMISSION=>"PERMISO MÉDICO",DocumentModule.VACATION=>"VACACIONES",DocumentModule.LEAVE=>"LICENCIA",_=>"0"};
        return new(person.Id,duty.Date,person.Population,"COMPLETED",value,cases,inputHash,output,trace,[]);
    }
}
