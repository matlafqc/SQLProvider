﻿namespace FSharp.Data.Sql.Providers

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Data
open System.Data.Odbc
open FSharp.Data.Sql
open FSharp.Data.Sql.Schema
open FSharp.Data.Sql.Common

type internal OdbcProvider(quotehcar : OdbcQuoteCharacter) =
    let pkLookup = ConcurrentDictionary<string,string list>()
    let tableLookup = ConcurrentDictionary<string,Table>()
    let columnLookup = ConcurrentDictionary<string,ColumnLookup>()

    let mutable typeMappings = []
    let mutable findClrType : (string -> TypeMapping option)  = fun _ -> failwith "!"
    let mutable findDbType : (string -> TypeMapping option)  = fun _ -> failwith "!"

    let quotes =
        if quotehcar = OdbcQuoteCharacter.NO_QUOTES then ' ',' '
        elif quotehcar = OdbcQuoteCharacter.GRAVE_ACCENT then '`', '`'
        elif quotehcar = OdbcQuoteCharacter.SQUARE_BRACKETS then '[', ']'
        elif quotehcar = OdbcQuoteCharacter.DOUBLE_QUOTES then '"', '"'
        elif quotehcar = OdbcQuoteCharacter.APHOSTROPHE then ''', '''
        else '`', '`'

    let mutable cOpen = fst quotes //char separator in query for table aliases `alias` or [alias]
    let mutable cClose = snd quotes

    let createTypeMappings (con:OdbcConnection) =

        if quotehcar = OdbcQuoteCharacter.DEFAULT_QUOTE && con.Driver.Contains("sql") then
            cOpen <- '['
            cClose <- ']'

        let dt = con.GetSchema("DataTypes")

        let getDbType(providerType:int) =
            let p = new OdbcParameter()
            p.OdbcType <- (Enum.ToObject(typeof<OdbcType>, providerType) :?> OdbcType)
            p.DbType

        let getClrType (input:string) = Type.GetType(input).ToString()

        let mappings =
            [
                for r in dt.Rows do
                    
                    if not(String.IsNullOrEmpty(r.["DataType"].ToString()) || String.IsNullOrEmpty(r.["TypeName"].ToString()) || String.IsNullOrEmpty(r.["ProviderDbType"].ToString())) then
                        let clrType = getClrType (string r.["DataType"])
                        let oleDbType = string r.["TypeName"]
                        let providerType = unbox<int> r.["ProviderDbType"]
                        let dbType = getDbType providerType
                        yield { ProviderTypeName = Some oleDbType; ClrType = clrType; DbType = dbType; ProviderType = Some providerType; }
                    
                    if r.Table <> null && r.Table.Columns.Contains("DataType")  && r.Table.Columns.Contains("TypeName")  && r.Table.Columns.Contains("ProviderDbType") then
                        let clrType = getClrType (r.Table.Columns.["DataType"].DataType.ToString())
                        let oleDbType = r.Table.Columns.["TypeName"].DataType.ToString()
                        let providerType = unbox<int> r.Table.Columns.["ProviderDbType"].Ordinal
                        let dbType = getDbType providerType
                        yield { ProviderTypeName = Some oleDbType; ClrType = clrType; DbType = dbType; ProviderType = Some providerType; }
            ]

        let clrMappings =
            mappings
            |> List.map (fun m -> m.ClrType, m)
            |> Map.ofList

        let dbMappings =
            mappings
            |> List.map (fun m -> m.ProviderTypeName.Value, m)
            |> Map.ofList

        typeMappings <- mappings
        findClrType <- clrMappings.TryFind
        findDbType <- dbMappings.TryFind

    let createInsertCommand (con:IDbConnection) (sb:Text.StringBuilder) (entity:SqlEntity) =
        let (~~) (t:string) = sb.Append t |> ignore
        let cmd = new OdbcCommand()
        cmd.Connection <- con :?> OdbcConnection
        let columnNames, values =
            (([],0),entity.ColumnValues)
            ||> Seq.fold(fun (out,i) (key,value) ->
                let name = sprintf "@param%i" i
                let p = OdbcParameter(name,value)
                (key,p)::out,i+1)
            |> fun (x,_)-> x
            |> List.rev
            |> List.toArray
            |> Array.unzip

        sb.Clear() |> ignore
        ~~(sprintf "INSERT INTO %c%s%c (%s) VALUES (%s);"
            cOpen entity.Table.Name cClose
            (String.Join(",",columnNames))
            (String.Join(",",values |> Array.map(fun _ -> "?"))))
        cmd.Parameters.AddRange(values)
        cmd.CommandText <- sb.ToString()
        cmd

    let lastInsertId (con:IDbConnection) =
        let cmd = new OdbcCommand()
        cmd.Connection <- con :?> OdbcConnection
        cmd.CommandText <- "SELECT @@IDENTITY AS id;"
        cmd

    let createUpdateCommand (con:IDbConnection) (sb:Text.StringBuilder) (entity:SqlEntity) (changedColumns: string list) =
        let (~~) (t:string) = sb.Append t |> ignore
        let cmd = new OdbcCommand()
        cmd.Connection <- con :?> OdbcConnection
        let haspk = pkLookup.ContainsKey(entity.Table.FullName)
        let pk = if haspk then pkLookup.[entity.Table.FullName] else []
        sb.Clear() |> ignore

        match pk with
        | [x] when changedColumns |> List.exists ((=)x)
            -> failwith "Error - you cannot change the primary key of an entity."
        | _ -> ()

        let pkValues =
            match entity.GetPkColumnOption<obj> pk with
            | [] -> failwith ("Error - you cannot update an entity that does not have a primary key. (" + entity.Table.FullName + ")")
            | v -> v

        let data =
            (([],0),changedColumns)
            ||> List.fold(fun (out,i) col ->
                let p =
                    match entity.GetColumnOption<obj> col with
                    | Some v -> OdbcParameter(null,v)
                    | None -> OdbcParameter(null,DBNull.Value)
                (col,p)::out,i+1)
            |> fun (x,_)-> x
            |> List.rev
            |> List.toArray

        match pk with
        | [] -> ()
        | [x] ->
            ~~(sprintf "UPDATE %c%s%c SET %s WHERE %s = ?;"
                cOpen entity.Table.Name cClose
                (String.Join(",", data |> Array.map(fun (c,_) -> sprintf "%c%s%c = %s" cOpen c cClose "?" ) ))
                x)
        | ks -> 
            // TODO: What is the ?-mark parameter? Look from other providers how this is done.
            failwith ("Composite key items update is not Supported in Odbc. (" + entity.Table.FullName + ")")

        cmd.Parameters.AddRange(data |> Array.map snd)

        pkValues |> List.iteri(fun i pkValue ->
            let pkParam = OdbcParameter(null, pkValue)
            cmd.Parameters.Add pkParam |> ignore
            )
        cmd.CommandText <- sb.ToString()
        cmd

    let createDeleteCommand (con:IDbConnection) (sb:Text.StringBuilder) (entity:SqlEntity) =
        let (~~) (t:string) = sb.Append t |> ignore
        let cmd = new OdbcCommand()
        cmd.Connection <- con :?> OdbcConnection
        sb.Clear() |> ignore
        let haspk = pkLookup.ContainsKey(entity.Table.FullName)
        let pk = if haspk then pkLookup.[entity.Table.FullName] else []
        sb.Clear() |> ignore
        let pkValues =
            match entity.GetPkColumnOption<obj> pk with
            | [] -> failwith ("Error - you cannot delete an entity that does not have a primary key. (" + entity.Table.FullName + ")")
            | v -> v

        pkValues |> List.iteri(fun i pkValue ->
            cmd.Parameters.AddWithValue("@id"+i.ToString(),pkValue) |> ignore)

        match pk with
        | [] -> ()
        | [k] -> ~~(sprintf "DELETE FROM %c%s%c WHERE %s = ?;" cOpen entity.Table.FullName cClose k )
        | ks -> 
            // TODO: What is the ?-mark parameter? Look from other providers how this is done.
            failwith ("Composite key items deletion is not Supported in Odbc. (" + entity.Table.FullName + ")")
        cmd.CommandText <- sb.ToString()
        cmd

    interface ISqlProvider with
        member __.CreateConnection(connectionString) = upcast new OdbcConnection(connectionString)
        member __.CreateCommand(connection,commandText) = upcast new OdbcCommand(commandText, connection:?>OdbcConnection)

        member __.CreateCommandParameter(param, value) =
            let p = OdbcParameter()
            p.Value <- value
            p.ParameterName <- param.Name
            p.DbType <- param.TypeMapping.DbType
            p.Direction <- param.Direction
            Option.iter (fun l -> p.Size <- l) param.Length
            upcast p

        member __.ExecuteSprocCommand(_,_,_,_) = ReturnValueType.Unit
        member __.CreateTypeMappings(con) = createTypeMappings (con:?>OdbcConnection)

        member __.GetTables(con,_) =
            let con = con :?> OdbcConnection
            if con.State <> ConnectionState.Open then con.Open()
            let dataTables = con.GetSchema("Tables").Rows |> Seq.cast<DataRow> |> Seq.map (fun i -> i.ItemArray)
            [ for dataTable in dataTables do
                let schema = 
                    if String.IsNullOrEmpty(dataTable.[1].ToString()) then
                        "dbo"
                    else string dataTable.[1]

                let table ={ Schema = schema ; Name = string dataTable.[2] ; Type=(string dataTable.[3]).ToLower() }
                yield tableLookup.GetOrAdd(table.FullName,table)
                ]

        member __.GetPrimaryKey(table) =
            match pkLookup.TryGetValue table.FullName with
            | true, [v] -> Some v
            | _ -> None

        member __.GetColumns(con,table) =
            match columnLookup.TryGetValue table.FullName with
            | (true,data) when data.Count > 0 -> data
            | _ ->
                let con = con :?> OdbcConnection
                if con.State <> ConnectionState.Open then con.Open()
                let primaryKey = con.GetSchema("Indexes", [| null; null; table.Name |]).Rows |> Seq.cast<DataRow> |> Seq.map (fun i -> i.ItemArray) |> Array.ofSeq
                let dataTable = con.GetSchema("Columns", [| null; null; table.Name; null|]).Rows |> Seq.cast<DataRow> |> Seq.map (fun i -> i.ItemArray)
                let columns =
                    [ for i in dataTable do
                        let dt = i.[5] :?> string
                        match findDbType dt with
                        | Some(m) ->
                            let name = i.[3] :?> string
                            let col =
                                { Column.Name = name
                                  TypeMapping = m
                                  IsNullable = let b = i.[17] :?> string in if b = "YES" then true else false
                                  IsPrimaryKey = if primaryKey.Length > 0 && primaryKey.[0].[8] = box name then true else false }
                            if col.IsPrimaryKey then 
                                pkLookup.AddOrUpdate(table.FullName, [col.Name], fun key old ->
                                    match col.Name with 
                                    | "" -> old 
                                    | x -> match old with
                                           | [] -> [x]
                                           | os -> x::os |> Seq.distinct |> Seq.toList |> List.sort
                                ) |> ignore
                            yield (col.Name,col)
                        | _ -> ()]
                    |> Map.ofList
                columnLookup.AddOrUpdate(table.FullName, columns, fun x old -> match columns.Count with 0 -> old | x -> columns)

        member __.GetRelationships(_,_) = ([],[]) // The ODBC type provider does not currently support GetRelationships operations.
        member __.GetSprocs(_) = []

        member __.GetIndividualsQueryText(table,_) =
            sprintf "SELECT * FROM %c%s%c" cOpen table.Name cClose

        member __.GetIndividualQueryText(table,column) =
            sprintf "SELECT * FROM %c%s%c WHERE %c%s%c.%c%s%c = ?" 
                     cOpen table.Name cClose cOpen table.Name cClose cOpen column cClose

        member __.GenerateQueryText(sqlQuery,baseAlias,baseTable,projectionColumns) =
            let sb = System.Text.StringBuilder()
            let parameters = ResizeArray<_>()
            let (~~) (t:string) = sb.Append t |> ignore

            let getTable x =
                match sqlQuery.Aliases.TryFind x with
                | Some(a) -> a
                | None -> baseTable

            let singleEntity = sqlQuery.Aliases.Count = 0

            let selectcolumns =
                if projectionColumns |> Seq.isEmpty then "1" else
                String.Join(",",
                    [|for KeyValue(k,v) in projectionColumns do
                        if v.Count = 0 then   // if no columns exist in the projection then get everything
                            for col in columnLookup.[(getTable k).FullName] |> Seq.map (fun c -> c.Key) do
                                if singleEntity then yield sprintf "%c%s%c" cOpen col cClose
                                else yield sprintf "%c%s%c.%c%s%c as %c%s_%s%c" cOpen k cClose cOpen col cClose cOpen k col cClose
                        else
                            for col in v do
                                if singleEntity then yield sprintf "%c%s%c" cOpen col cClose
                                else yield sprintf "%c%s%c.%c%s%c as %c%s_%s%c" cOpen k cClose cOpen col cClose cOpen k col cClose |]) // F# makes this so easy :)

            // Create sumBy, minBy, maxBy, ... field columns
            let columns =
                let extracolumns =
                    let fieldNotation(al:alias,col:string) =
                        match String.IsNullOrEmpty(al) with
                        | true -> sprintf "%c%s%c" cOpen col cClose
                        | false -> sprintf "%c%s%c.%c%s%c" cOpen al cClose cOpen col cClose
                    let fieldNotationAlias(al:alias,col:string) =
                        match String.IsNullOrEmpty(al) with
                        | true -> sprintf "%c%s%c" cOpen col cClose
                        | false -> sprintf "%c%s_%s%c" cOpen al col cClose

                    match sqlQuery.Grouping with
                    | [] -> FSharp.Data.Sql.Common.Utilities.parseAggregates fieldNotation fieldNotationAlias sqlQuery.AggregateOp
                    | g  -> 
                        let keys = g |> List.map(fst) |> List.concat |> List.map(fieldNotation)
                        let aggs = g |> List.map(snd) |> List.concat
                        let res2 = FSharp.Data.Sql.Common.Utilities.parseAggregates fieldNotation fieldNotationAlias aggs |> List.toSeq
                        [String.Join(", ", keys) + (match aggs with [] -> "" | _ -> ", ") + String.Join(", ", res2)] 
                match extracolumns with
                | [] -> selectcolumns
                | h::t -> h

            // make this nicer later.. just try and get the damn thing to work properly (well, at all) for now :D
            // NOTE: really need to assign the parameters their correct sql types

            let createParam (value:obj) =
                let paramName = "?"
                OdbcParameter(paramName,value):> IDbDataParameter

            let rec filterBuilder = function
                | [] -> ()
                | (cond::conds) ->
                    let build op preds (rest:Condition list option) =
                        ~~ "("
                        preds |> List.iteri( fun i (alias,col,operator,data) ->
                                let extractData data =
                                     match data with
                                     | Some(x) when (box x :? System.Linq.IQueryable) -> [||]
                                     | Some(x) when (box x :? obj array) ->
                                         // in and not in operators pass an array
                                         let strings = box x :?> obj array
                                         strings |> Array.map createParam
                                     | Some(x) -> [|createParam (box x)|]
                                     | None ->    [|createParam DBNull.Value|]

                                let prefix = if i>0 then (sprintf " %s " op) else ""
                                let paras = extractData data
                                ~~(sprintf "%s%s" prefix <|
                                    match operator with
                                    | FSharp.Data.Sql.IsNull -> (sprintf "%c%s%c.%c%s%c IS NULL") cOpen alias cClose cOpen col cClose
                                    | FSharp.Data.Sql.NotNull -> (sprintf "%c%s%c.%c%s%c IS NOT NULL") cOpen alias cClose cOpen col cClose
                                    | FSharp.Data.Sql.In ->
                                        let text = String.Join(",",paras |> Array.map (fun p -> p.ParameterName))
                                        Array.iter parameters.Add paras
                                        (sprintf "%c%s%c.%c%s%c IN (%s)") cOpen alias cClose cOpen col cClose text
                                    | FSharp.Data.Sql.NestedIn when data.IsSome ->
                                        let innersql, innerpars = data.Value |> box :?> string * IDbDataParameter[]
                                        Array.iter parameters.Add innerpars
                                        (sprintf "%c%s%c.%c%s%c IN (%s)") cOpen alias cClose cOpen col cClose innersql
                                    | FSharp.Data.Sql.NotIn ->
                                        let text = String.Join(",",paras |> Array.map (fun p -> p.ParameterName))
                                        Array.iter parameters.Add paras
                                        (sprintf "%c%s%c.%c%s%c NOT IN (%s)") cOpen alias cClose cOpen col cClose text
                                    | FSharp.Data.Sql.NestedNotIn when data.IsSome ->
                                        let innersql, innerpars = data.Value |> box :?> string * IDbDataParameter[]
                                        Array.iter parameters.Add innerpars
                                        (sprintf "%c%s%c.%c%s%c NOT IN (%s)") cOpen alias cClose cOpen col cClose innersql
                                    | _ ->
                                        parameters.Add paras.[0]
                                        (sprintf "%c%s%c.%s %s %s") cOpen alias cClose col
                                         (operator.ToString()) paras.[0].ParameterName)
                        )
                        // there's probably a nicer way to do this
                        let rec aux = function
                            | x::[] when preds.Length > 0 ->
                                ~~ (sprintf " %s " op)
                                filterBuilder [x]
                            | x::[] -> filterBuilder [x]
                            | x::xs when preds.Length > 0 ->
                                ~~ (sprintf " %s " op)
                                filterBuilder [x]
                                ~~ (sprintf " %s " op)
                                aux xs
                            | x::xs ->
                                filterBuilder [x]
                                ~~ (sprintf " %s " op)
                                aux xs
                            | [] -> ()

                        Option.iter aux rest
                        ~~ ")"

                    match cond with
                    | Or(preds,rest) -> build "OR" preds rest
                    | And(preds,rest) ->  build "AND" preds rest
                    | ConstantTrue -> ~~ " (1=1) "
                    | ConstantFalse -> ~~ " (1=0) "

                    filterBuilder conds

            // next up is the FROM statement which includes joins ..
            let fromBuilder() =
                sqlQuery.Links
                |> List.iter(fun (fromAlias, data, destAlias)  ->
                    let joinType = if data.OuterJoin then "LEFT OUTER JOIN " else "INNER JOIN "
                    let destTable = getTable destAlias
                    ~~  (sprintf "%s %c%s%c as %c%s%c on "
                            joinType cOpen destTable.Name cClose cOpen destAlias cClose)
                    ~~  (String.Join(" AND ", (List.zip data.ForeignKey data.PrimaryKey) |> List.map(fun (foreignKey,primaryKey) ->
                        sprintf "%c%s%c.%c%s%c = %c%s%c.%c%s%c "
                            cOpen
                            (if data.RelDirection = RelationshipDirection.Parents then fromAlias else destAlias)
                            cClose cOpen foreignKey cClose cOpen
                            (if data.RelDirection = RelationshipDirection.Parents then destAlias else fromAlias)
                            cClose cOpen primaryKey cClose))))

            let groupByBuilder() =
                sqlQuery.Grouping |> List.map(fst) |> List.concat
                |> List.iteri(fun i (alias,column) ->
                    if i > 0 then ~~ ", "
                    ~~ (sprintf "%c%s%c.%c%s%c" cOpen alias cClose cOpen column cClose ))

            let orderByBuilder() =
                sqlQuery.Ordering
                |> List.iteri(fun i (alias,column,desc) ->
                    if i > 0 then ~~ ", "
                    ~~ (sprintf "%c%s%c.%c%s%c %s" cOpen alias cClose cOpen column cClose (if not desc then "DESC" else "")))

            // Certain ODBC drivers (excel) don't like special characters in aliases, so we need to strip them
            // or else it will fail
            let stripSpecialCharacters (s:string) =
                String(s.ToCharArray() |> Array.filter(fun c -> Char.IsLetterOrDigit c || c = ' ' || c = '_'))

            // SELECT
            if sqlQuery.Distinct then ~~(sprintf "SELECT DISTINCT %s " columns)
            elif sqlQuery.Count then ~~("SELECT COUNT(1) ")
            else  ~~(sprintf "SELECT %s " columns)
            // FROM
            ~~(sprintf "FROM %c%s%c as %c%s%c " cOpen baseTable.Name cClose cOpen (stripSpecialCharacters baseAlias) cClose)
            fromBuilder()
            // WHERE
            if sqlQuery.Filters.Length > 0 then
                // each filter is effectively the entire contents of each where clause in the linq query,
                // of which there can be many. Simply turn them all into one big AND expression as that is the
                // only logical way to deal with them.
                let f = [And([],Some sqlQuery.Filters)]
                ~~"WHERE "
                filterBuilder f

            // GROUP BY
            if sqlQuery.Grouping.Length > 0 then
                ~~" GROUP BY "
                groupByBuilder()

            // ORDER BY
            if sqlQuery.Ordering.Length > 0 then
                ~~"ORDER BY "
                orderByBuilder()

            match sqlQuery.Union with
            | Some(true, suquery) -> ~~(sprintf " UNION ALL %s " suquery)
            | Some(false, suquery) -> ~~(sprintf " UNION %s " suquery)
            | None -> ()

            let sql = sb.ToString()
            (sql,parameters)

        member this.ProcessUpdates(con, entities) =
            let sb = Text.StringBuilder()

            CommonTasks.``ensure columns have been loaded`` (this :> ISqlProvider) con entities

            if entities.Count = 0 then 
                ()
            else

            if con.State <> ConnectionState.Open then con.Open()

            use scope = Utilities.ensureTransaction()
            try
                // close the connection first otherwise it won't get enlisted into the transaction
                if con.State = ConnectionState.Open then con.Close()
                con.Open()
                // initially supporting update/create/delete of single entities, no hierarchies yet
                entities.Keys
                |> Seq.iter(fun e ->
                    match e._State with
                    | Created ->
                        let cmd = createInsertCommand con sb e
                        Common.QueryEvents.PublishSqlQuery cmd.CommandText
                        cmd.ExecuteNonQuery() |> ignore
                        let id = (lastInsertId con).ExecuteScalar()
                        CommonTasks.checkKey pkLookup id e
                        e._State <- Unchanged
                    | Modified fields ->
                        let cmd = createUpdateCommand con sb e fields
                        Common.QueryEvents.PublishSqlQuery cmd.CommandText
                        cmd.ExecuteNonQuery() |> ignore
                        e._State <- Unchanged
                    | Delete ->
                        let cmd = createDeleteCommand con sb e
                        Common.QueryEvents.PublishSqlQuery cmd.CommandText
                        cmd.ExecuteNonQuery() |> ignore
                        // remove the pk to prevent this attempting to be used again
                        e.SetPkColumnOptionSilent(pkLookup.[e.Table.FullName], None)
                        e._State <- Deleted
                    | Deleted | Unchanged -> failwith "Unchanged entity encountered in update list - this should not be possible!")
                scope.Complete()

            finally
                con.Close()

        member this.ProcessUpdatesAsync(con, entities) =
            let sb = Text.StringBuilder()

            CommonTasks.``ensure columns have been loaded`` (this :> ISqlProvider) con entities

            if entities.Count = 0 then 
                async { () }
            else

            async {
                use scope = Utilities.ensureTransaction()
                try
                    // close the connection first otherwise it won't get enlisted into the transaction
                    if con.State = ConnectionState.Open then con.Close()

                    do! con.OpenAsync() |> Async.AwaitIAsyncResult |> Async.Ignore
                    // initially supporting update/create/delete of single entities, no hierarchies yet
                    let handleEntity (e: SqlEntity) =
                        match e._State with
                        | Created ->
                            async {
                                let cmd = createInsertCommand con sb e
                                Common.QueryEvents.PublishSqlQuery cmd.CommandText
                                do! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore
                                let id = (lastInsertId con).ExecuteScalar()
                                CommonTasks.checkKey pkLookup id e
                                e._State <- Unchanged
                            }
                        | Modified fields ->
                            async {
                                let cmd = createUpdateCommand con sb e fields
                                Common.QueryEvents.PublishSqlQuery cmd.CommandText
                                do! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore
                                e._State <- Unchanged
                            }
                        | Delete ->
                            async {
                                let cmd = createDeleteCommand con sb e
                                Common.QueryEvents.PublishSqlQuery cmd.CommandText
                                do! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore
                                // remove the pk to prevent this attempting to be used again
                                e.SetPkColumnOptionSilent(pkLookup.[e.Table.FullName], None)
                                e._State <- Deleted
                            }
                        | Deleted | Unchanged -> failwith "Unchanged entity encountered in update list - this should not be possible!"
                    do! Utilities.executeOneByOne handleEntity (entities.Keys|>Seq.toList)
                    scope.Complete()

                finally
                    con.Close()
            }
