﻿namespace FSharp.Data.Sql.QueryExpression

open System
open System.Linq.Expressions
open System.Collections.Generic

open FSharp.Data.Sql.Common
open FSharp.Data.Sql.Patterns
open FSharp.Data.Sql.Schema

module internal QueryExpressionTransformer =

    type GroupResultItems<'key, 'itm>(k, collection:seq<'itm>) =
        inherit ResizeArray<'itm> (collection) 
        member __.Values = collection
        interface System.Linq.IGrouping<'key, 'itm> with
             member __.Key = k

    let myLock = new Object();
    let getSubEntityMi =
        match <@ (Unchecked.defaultof<SqlEntity>).GetSubTable("", "") @> with
        | FSharp.Quotations.Patterns.Call(_,mi,_) -> mi
        | _ -> failwith "never"

    let transform (projection:Expression) (tupleIndex:string ResizeArray) (databaseParam:ParameterExpression) (aliasEntityDict:Map<string,Table>) (ultimateChild:(string * Table) option) (replaceParams:Dictionary<ParameterExpression, LambdaExpression>) =
        let (|SingleTable|MultipleTables|) = function
            | MethodCall(None, MethodWithName "Select", [Constant(_, t) ;exp]) when t = typeof<System.Linq.IQueryable<SqlEntity>> || t = typeof<System.Linq.IOrderedQueryable<SqlEntity>> ->
                SingleTable exp
            | MethodCall(None, MethodWithName "Select", [_ ;exp]) ->
                MultipleTables exp
            | _ -> failwith ("Unsupported projection: " + projection.NodeType.ToString())

            // 1. only one table was involed so the input is a single parameter
            // 2. the input is a n tuple returned from the query
            // in both cases we need to work out what columns were selected from the different tables,
            //   and if at any point a whole table is selected, that should take precedence over any
            //   previously selected individual columns.
            // in the second case we also need to change any property on the input tuple into calls
            // onto GetSubEntity on the result parameter with the correct alias

        let projectionMap = Dictionary<string,string ResizeArray>()
        
        let (|SourceTupleGet|_|) (e:Expression) =
            match e with
            | PropertyGet(Some(ParamWithName "tupledArg"), info) when info.PropertyType = typeof<SqlEntity> ->
                let alias = Utilities.resolveTuplePropertyName (e :?> MemberExpression).Member.Name tupleIndex
                if aliasEntityDict.ContainsKey(alias) then
                    Some (alias,aliasEntityDict.[alias].FullName, None)
                elif ultimateChild.IsSome then
                    Some (alias, fst(ultimateChild.Value), None)
                else None

            | MethodCall(Some(PropertyGet(Some(ParamWithName "tupledArg"),info) as getter),
                         (MethodWithName "GetColumn" | MethodWithName "GetColumnOption" as mi) ,
                         [String key]) when info.PropertyType = typeof<SqlEntity> ->
                let alias = Utilities.resolveTuplePropertyName (getter :?> MemberExpression).Member.Name tupleIndex
                if aliasEntityDict.ContainsKey(alias) then
                    Some (alias,aliasEntityDict.[alias].FullName, Some(key,mi))
                elif ultimateChild.IsSome then
                    Some (alias,fst(ultimateChild.Value), Some(key,mi))
                else None
            | _ -> None

        // this is not tail recursive but it shouldn't matter in practice ....
        let rec transform  (en:String option) (e:Expression): Expression =
            let e = ExpressionOptimizer.doReduction e
            if e = null then null else
            match e.NodeType, e with
            | _, SourceTupleGet(alias,name,None) ->
                // at any point if we see a property getter where the input is "tupledArg" this
                // needs to be replaced with a call to GetSubEntity using the result as an input
                match projectionMap.TryGetValue alias with
                | true, values -> values.Clear()
                | false, _ -> projectionMap.Add(alias,new ResizeArray<_>())
                upcast Expression.Call(databaseParam,getSubEntityMi,Expression.Constant(alias),Expression.Constant(name))
            | _, SourceTupleGet(alias,name,Some(key,mi)) ->
                match projectionMap.TryGetValue alias with
                | true, values when values.Count > 0 -> values.Add(key)
                | false, _ -> projectionMap.Add(alias,new ResizeArray<_>(seq{yield key}))
                | _ -> ()
                upcast
                    Expression.Call(
                        Expression.Call(databaseParam,getSubEntityMi,Expression.Constant(alias),Expression.Constant(name)),
                            mi,Expression.Constant(key))

            | ExpressionType.Call, MethodCall(Some(ParamName name),(MethodWithName "GetColumn" | MethodWithName "GetColumnOption" as mi),[String key])  ->
                match projectionMap.TryGetValue name with
                | true, values when values.Count > 0 -> values.Add(key)
                | false, _ -> projectionMap.Add(name,new ResizeArray<_>(seq{yield key}))
                | _ -> ()
                upcast Expression.Call(databaseParam,mi,Expression.Constant(key))

            | ExpressionType.Negate,             (:? UnaryExpression as e)       -> upcast Expression.Negate(transform en e.Operand,e.Method)
            | ExpressionType.NegateChecked,      (:? UnaryExpression as e)       -> upcast Expression.NegateChecked(transform en e.Operand,e.Method)
            | ExpressionType.Not,                (:? UnaryExpression as e)       -> upcast Expression.Not(transform en e.Operand,e.Method)
            | ExpressionType.Convert,            (:? UnaryExpression as e)       -> upcast Expression.Convert(transform en e.Operand,e.Type)
            | ExpressionType.ConvertChecked,     (:? UnaryExpression as e)       -> upcast Expression.ConvertChecked(transform en e.Operand,e.Type)
            | ExpressionType.ArrayLength,        (:? UnaryExpression as e)       -> upcast Expression.ArrayLength(transform en e.Operand)
            | ExpressionType.Quote,              (:? UnaryExpression as e)       -> upcast Expression.Quote(transform en e.Operand)
            | ExpressionType.TypeAs,             (:? UnaryExpression as e)       -> upcast Expression.TypeAs(transform en e.Operand,e.Type)
            | ExpressionType.Add,                (:? BinaryExpression as e)  when e.Left.Type = typeof<string> && e.Right.Type = typeof<string> -> 
                                                                             // http://stackoverflow.com/questions/7027384/the-binary-operator-add-is-not-defined-for-the-types-system-string-and-syste
                                                                             let concatMethod = typeof<string>.GetMethod("Concat", [| typeof<string>; typeof<string> |]); 
                                                                             upcast Expression.Add(transform en e.Left, transform en e.Right, concatMethod)
            | ExpressionType.Add,                (:? BinaryExpression as e)      -> upcast Expression.Add(transform en e.Left, transform en e.Right)
            | ExpressionType.AddChecked,         (:? BinaryExpression as e)      -> upcast Expression.AddChecked(transform en e.Left, transform en e.Right)
            | ExpressionType.Subtract,           (:? BinaryExpression as e)      -> upcast Expression.Subtract(transform en e.Left, transform en e.Right)
            | ExpressionType.SubtractChecked,    (:? BinaryExpression as e)      -> upcast Expression.SubtractChecked(transform en e.Left, transform en e.Right)
            | ExpressionType.Multiply,           (:? BinaryExpression as e)      -> upcast Expression.Multiply(transform en e.Left, transform en e.Right)
            | ExpressionType.MultiplyChecked,    (:? BinaryExpression as e)      -> upcast Expression.MultiplyChecked(transform en e.Left, transform en e.Right)
            | ExpressionType.Divide,             (:? BinaryExpression as e)      -> upcast Expression.Divide(transform en e.Left, transform en e.Right)
            | ExpressionType.Modulo,             (:? BinaryExpression as e)      -> upcast Expression.Modulo(transform en e.Left, transform en e.Right)
            | ExpressionType.And,                (:? BinaryExpression as e)      -> upcast Expression.And(transform en e.Left, transform en e.Right)
            | ExpressionType.AndAlso,            (:? BinaryExpression as e)      -> upcast Expression.AndAlso(transform en e.Left, transform en e.Right)
            | ExpressionType.Or,                 (:? BinaryExpression as e)      -> upcast Expression.Or(transform en e.Left, transform en e.Right)
            | ExpressionType.OrElse,             (:? BinaryExpression as e)      -> upcast Expression.OrElse(transform en e.Left, transform en e.Right)
            | ExpressionType.LessThan,           (:? BinaryExpression as e)      -> upcast Expression.LessThan(transform en e.Left, transform en e.Right)
            | ExpressionType.LessThanOrEqual,    (:? BinaryExpression as e)      -> upcast Expression.LessThanOrEqual(transform en e.Left, transform en e.Right)
            | ExpressionType.GreaterThan,        (:? BinaryExpression as e)      -> upcast Expression.GreaterThan(transform en e.Left, transform en e.Right)
            | ExpressionType.GreaterThanOrEqual, (:? BinaryExpression as e)      -> upcast Expression.GreaterThanOrEqual(transform en e.Left, transform en e.Right)
            | ExpressionType.Equal,              (:? BinaryExpression as e)      -> upcast Expression.Equal(transform en e.Left, transform en e.Right)
            | ExpressionType.NotEqual,           (:? BinaryExpression as e)      -> upcast Expression.NotEqual(transform en e.Left, transform en e.Right)
            | ExpressionType.Coalesce,           (:? BinaryExpression as e)   when e.Conversion <> null -> 
                                                                                    let left = transform en e.Left
                                                                                    let right = transform en e.Right
                                                                                    let conv = transform en e.Conversion
                                                                                    upcast Expression.Coalesce(left, right, conv :?> LambdaExpression)
            | ExpressionType.Coalesce,           (:? BinaryExpression as e)      -> upcast Expression.Coalesce(transform en e.Left, transform en e.Right)
            | ExpressionType.ArrayIndex,         (:? BinaryExpression as e)      -> upcast Expression.ArrayIndex(transform en e.Left, transform en e.Right)
            | ExpressionType.RightShift,         (:? BinaryExpression as e)      -> upcast Expression.RightShift(transform en e.Left, transform en e.Right)
            | ExpressionType.LeftShift,          (:? BinaryExpression as e)      -> upcast Expression.LeftShift(transform en e.Left, transform en e.Right)
            | ExpressionType.ExclusiveOr,        (:? BinaryExpression as e)      -> upcast Expression.ExclusiveOr(transform en e.Left, transform en e.Right)
            | ExpressionType.TypeIs,             (:? TypeBinaryExpression as e)  -> upcast Expression.TypeIs(transform en e.Expression, e.Type)
            | ExpressionType.Conditional,        (:? ConditionalExpression as e) -> let testExp = transform en e.Test
                                                                                    match testExp with // For now, only direct booleans conditions are optimized to select query:
                                                                                    | :? ConstantExpression as c when c.Value = box(true) -> transform en e.IfTrue
                                                                                    | :? ConstantExpression as c when c.Value = box(false) -> transform en e.IfFalse
                                                                                    | _ -> upcast Expression.Condition(testExp, transform en e.IfTrue, transform en e.IfFalse)
            | ExpressionType.Constant,           (:? ConstantExpression as e)    -> upcast e
            | ExpressionType.Parameter,          (:? ParameterExpression as e)   -> match en with
                                                                                    | Some(en) when en = e.Name && (replaceParams=null || not(replaceParams.ContainsKey(e))) ->
                                                                                         match projectionMap.TryGetValue en with
                                                                                         | true, values -> values.Clear()
                                                                                         | false, _ -> projectionMap.Add(en,new ResizeArray<_>())
                                                                                         upcast databaseParam
                                                                                    | _ ->
                                                                                        if replaceParams<>null && replaceParams.ContainsKey(e) then
                                                                                            replaceParams.[e].Body
                                                                                        else
                                                                                            upcast e
            | ExpressionType.MemberAccess,       (:? MemberExpression as e)      -> let memb = Expression.MakeMemberAccess(transform en e.Expression, e.Member)
                                                                                    // If we have merged new lambdas, just check the combination of anonymous objects
                                                                                    if replaceParams.Count>0 then
                                                                                        ExpressionOptimizer.Methods.``remove AnonymousType`` memb
                                                                                    else upcast memb
            | ExpressionType.Call,               (:? MethodCallExpression as e)  -> upcast Expression.Call( (if e.Object = null then null else transform en e.Object), e.Method, e.Arguments |> Seq.map(fun a -> transform en a))
            | ExpressionType.Lambda,             (:? LambdaExpression as e)      -> let exType = e.GetType()
                                                                                    if  exType.IsGenericType
                                                                                        && exType.GetGenericTypeDefinition() = typeof<Expression<obj>>.GetGenericTypeDefinition()
                                                                                        && exType.GenericTypeArguments.[0].IsSubclassOf typeof<Delegate> then
                                                                                        upcast Expression.Lambda(e.GetType().GenericTypeArguments.[0],transform en e.Body, e.Parameters)
                                                                                    else
                                                                                        upcast Expression.Lambda(transform en e.Body, e.Parameters)
            | ExpressionType.New,                (:? NewExpression as e)         -> if e.Members = null then
                                                                                      upcast Expression.New(e.Constructor, e.Arguments |> Seq.map(fun a -> transform en a))
                                                                                    else
                                                                                      upcast Expression.New(e.Constructor, e.Arguments |> Seq.map(fun a -> transform en a), e.Members)
            | ExpressionType.NewArrayInit,       (:? NewArrayExpression as e)    -> upcast Expression.NewArrayInit(e.Type.GetElementType(), e.Expressions |> Seq.map(fun e -> transform en e))
            | ExpressionType.NewArrayBounds,     (:? NewArrayExpression as e)    -> upcast Expression.NewArrayBounds(e.Type.GetElementType(), e.Expressions |> Seq.map(fun e -> transform en e))
            | ExpressionType.Invoke,             (:? InvocationExpression as e)  -> upcast Expression.Invoke(transform en e.Expression, e.Arguments |> Seq.map(fun a -> transform en a))
            | ExpressionType.MemberInit,         (:? MemberInitExpression as e)  -> upcast Expression.MemberInit( (transform en e.NewExpression) :?> NewExpression , e.Bindings)
            | ExpressionType.ListInit,           (:? ListInitExpression as e)    -> upcast Expression.ListInit( (transform en e.NewExpression) :?> NewExpression, e.Initializers)
            | _ -> failwith ("encountered unknown LINQ expression: " + e.NodeType.ToString() + " " + e.ToString())

        let newProjection =
            match projection with
            | SingleTable(OptionalQuote(Lambda([ParamName _],ParamName x))) ->
                projectionMap.Add(x,ResizeArray<_>())
                Expression.Lambda(databaseParam,[databaseParam]) :> Expression
            | SingleTable(OptionalQuote(Lambda([ParamName x], (NewExpr(ci, args ) )))) ->
                Expression.Lambda(Expression.New(ci, (List.map (transform (Some x)) args)),[databaseParam]) :> Expression
            | SingleTable(OptionalQuote(lambda))
            | MultipleTables(OptionalQuote(lambda)) -> transform None lambda

        newProjection, projectionMap

    let convertExpression exp (entityIndex:string ResizeArray) con (provider:ISqlProvider) =
        // first convert the abstract query tree into a more useful format
        let legaliseName (alias:alias) =
                if alias.StartsWith("_") then alias.TrimStart([|'_'|]) else alias

        let entityIndex = new ResizeArray<_>(entityIndex |> Seq.map (legaliseName))

        let sqlQuery = SqlQuery.ofSqlExp(exp,entityIndex)

         // note : the baseAlias here will always be "" when no criteria has been applied, because the LINQ tree never needed to refer to it
        let baseAlias,baseTable =
            match sqlQuery.UltimateChild with
            | Some(baseAlias,baseTable) when baseAlias = ""-> (baseTable.Name,baseTable)
            | Some(baseAlias,baseTable) -> (baseAlias,baseTable)
            | _ -> failwith ("Unknown sqlQuery.UltimateChild: " + sqlQuery.UltimateChild.ToString())

        let (projectionDelegate,projectionColumns) =
            match sqlQuery.Projection with
            | [] ->

                // this case happens when there are only where clauses with a single table and a projection containing just the table's entire rows. example:
                // for x in dc.john
                // where x.y = 1
                // select x
                // this does not create a call to .select() after .where(), therefore in this case we must provide our own projection that simply selects a whole row
                let initDbParam = Expression.Parameter(typeof<SqlEntity>,"result")
                let pmap = Dictionary<string,string ResizeArray>()
                pmap.Add(baseAlias, new ResizeArray<_>())
                (Expression.Lambda(initDbParam,initDbParam).Compile(),pmap)
            | projs -> 
                let replaceParams = Dictionary<ParameterExpression, LambdaExpression>()

                // We should fetch the initial parameter type from the fist lambda input type.
                // But currently SQL will always return a list of SqlEntities.

                let initDbParam = 
                    // Todo: GroupBy: The current join breaks here, as it fakes anonymous object to be SqlEntity by skipping projection lambda.

                    // Usually it's just SqlEntity but it can be also tuple in joins etc.
                    let rec foundInitParamType : Expression -> ParameterExpression = function
                        | :? LambdaExpression as lambda when lambda.Parameters.Count = 1 ->
                            let t = lambda.Parameters.[0].Type
                            Expression.Parameter(lambda.Parameters.[0].Type,"result")
                        | :? MethodCallExpression as meth when meth.Arguments.Count = 1 ->
                            Expression.Parameter(meth.Arguments.[0].Type,"result")
                        | :? UnaryExpression as ce -> 
                            foundInitParamType ce.Operand
                        | _ -> Expression.Parameter(typeof<SqlEntity>,"result")

                    match projs.Head with
                    // We have this wrap and the lambda is the second argument:
                    | :? MethodCallExpression as meth when meth.Arguments.Count = 2 ->
                        foundInitParamType meth.Arguments.[1]
                    | _ -> foundInitParamType projs.Head

                // Multiple projections found. We need to do a function composition: prevProj >> currentProj
                // for Lamda Expressions. So this is an expression tree visitor.
                // What we actually do is go through these lambda-expressions one by one and say:
                // - Find any parameters.
                // - If they are database parameters:
                //   1) Add them to parameter list. We will parse this list to database parameters to SQL-clause
                //   2) For each of these parameters we need to also collect the lambda that is run after the SQL returns.
                // - And if it's a non-database parameter, it's a param from previous lambda-expression. 
                //   So replace the parameter with the previous lambda expression (which might have a database parameter)
                let visitExpression (currentProj:Expression) (prevProj:Expression) (dbParam:ParameterExpression) =
                    
                    let rec generateReplacementParams (proj:Expression) = 
                        match proj.NodeType, proj with
                        | ExpressionType.Lambda, (:? LambdaExpression as lambda) ->  
                            match prevProj with
                            | :? LambdaExpression as prevLambda when prevLambda <> Unchecked.defaultof<LambdaExpression> -> 
                                lambda.Parameters |> Seq.iter(fun p -> replaceParams.[p] <- prevLambda)
                            | _ when prevProj = Unchecked.defaultof<Expression> -> 
                                lambda.Parameters |> Seq.iter(fun p -> replaceParams.[p] <- Expression.Lambda(initDbParam,initDbParam))
                            | _ -> ()
                        | ExpressionType.Quote, (:? UnaryExpression as ce) ->  
                            generateReplacementParams ce.Operand
                        | ExpressionType.Call, (:? MethodCallExpression as me) ->  
                            me.Arguments |> Seq.iter(fun a -> generateReplacementParams a)
                        | _ -> ()

                    generateReplacementParams(currentProj)
                    let newProjection, projectionMap = transform currentProj entityIndex dbParam sqlQuery.Aliases sqlQuery.UltimateChild replaceParams
                    let fixedParams = Expression.Lambda((newProjection:?>LambdaExpression).Body,initDbParam)
                    //QueryEvents.PublishExpression fixedParams
                    fixedParams,projectionMap
                
                let rec composeProjections projs prevLambda (foundparams : Dictionary<string, ResizeArray<string>>) = 
                    match projs with 
                    | [] -> prevLambda, foundparams
                    | proj::tail -> 
                        let lambda1, dbparams1 = visitExpression proj prevLambda initDbParam
                        dbparams1 |> Seq.iter(fun k -> foundparams.[k.Key] <- k.Value )
                        composeProjections tail lambda1 foundparams

                let generatedMegaLambda, finalParams = composeProjections projs (Unchecked.defaultof<LambdaExpression>) (Dictionary<string, ResizeArray<string>>())
                QueryEvents.PublishExpression generatedMegaLambda
                (generatedMegaLambda.Compile(),finalParams)

        // a special case here to handle queries that start from the relationship of an individual
        let sqlQuery,baseAlias =
            if sqlQuery.Aliases.Count = 0 then
               let alias =
                   match projectionColumns.Keys.Count with
                   | 0 -> baseAlias
                   | 1 -> projectionColumns.Keys |> Seq.head
                   | x -> // Multiple aliases for same basetable name. We have to select one and merge columns.
                        let starSelect = projectionColumns |> Seq.tryPick(fun p -> match p.Value.Count with | 0 -> Some p | _ -> None)
                        match starSelect with
                        | Some sel ->
                            let tmp = sel
                            projectionColumns.Clear()
                            projectionColumns.Add(tmp.Key, tmp.Value)
                            tmp.Key
                        | None ->
                            let itms = projectionColumns |> Seq.map(fun p -> p.Value) |> Seq.concat |> Seq.distinct |> Seq.toList
                            let selKey = (projectionColumns.Keys |> Seq.head)
                            projectionColumns.Clear()
                            projectionColumns.Add(selKey, new ResizeArray<string>(itms))
                            projectionColumns.Keys |> Seq.head

               { sqlQuery with UltimateChild = Some(alias,snd sqlQuery.UltimateChild.Value) }, alias
            else sqlQuery,baseAlias

        let resolve name =
            // name will be blank when there is only a single table as it never gets
            // tupled by the LINQ infrastructure. In this case we know it must be referring
            // to the only table in the query, so replace it
            if String.IsNullOrWhiteSpace(name) || name = "__base__" then (fst sqlQuery.UltimateChild.Value)
            else 
                let tbl = Utilities.resolveTuplePropertyName name entityIndex
                if tbl = "" then baseAlias else tbl

        let rec resolveFilterList = function
            | And(xs,y) -> And(xs|>List.map(fun (a,b,c,d) -> resolve a,b,c,d),Option.map (List.map resolveFilterList) y)
            | Or(xs,y) -> Or(xs|>List.map(fun (a,b,c,d) -> resolve a,b,c,d),Option.map (List.map resolveFilterList) y)
            | ConstantTrue -> ConstantTrue
            | ConstantFalse -> ConstantFalse

        // the crazy LINQ infrastructure does all kinds of weird things with joins which means some information
        // is lost up and down the expression tree, but now with all the data available we can resolve the problems...

        // 1.
        // re-map the tuple arg names to their proper aliases in the filters
        // its possible to do this when converting the expression but its
        // much easier at this stage once we have knowledge of the whole query
        let sqlQuery = { sqlQuery with Filters = List.map resolveFilterList sqlQuery.Filters
                                       Ordering = List.map(function ("",b,c) -> (resolve "",b,c) | x -> x) sqlQuery.Ordering }

        // 2.
        // Some aliases will have blank table information, but these can be resolved by looking
        // in the link data or ultimate base entity
        let resolveAlias alias table =
            if table.Name <> "" then table else
            match sqlQuery.UltimateChild with
            | Some(uc) when alias = fst uc -> snd uc
            | _ -> sqlQuery.Links
                   |> List.pick(fun (_,linkData,innerAlias) -> if innerAlias = alias then Some(linkData.PrimaryTable) else None)
        let sqlQuery = { sqlQuery with Aliases = Map.map resolveAlias sqlQuery.Aliases }

        // 3.
        // Some link data will be missing its foreign table data which needs setting to the resolved table of the
        // outer alias - this happens depending on the which way the join is around - information is "lost" up the tree which
        // able to be resolved now.
        let resolveLinks (outerAlias:alias, linkData:LinkData, innerAlias) =
            let resolved = sqlQuery.Aliases.[outerAlias]
            if linkData.ForeignTable.Name <> "" then (outerAlias, linkData, innerAlias)
            else (outerAlias, { linkData with ForeignTable = resolved }, innerAlias)
        let sqlQuery = { sqlQuery with Links = List.map resolveLinks sqlQuery.Links }

        // make sure the provider has cached the columns for the tables within the projection
        projectionColumns
        |> Seq.iter(function KeyValue(k,_) ->
                                let table = match sqlQuery.Aliases.TryFind k with
                                            | Some v -> v
                                            | None -> snd sqlQuery.UltimateChild.Value
                                lock myLock (fun () -> provider.GetColumns (con,table) |> ignore ))

        let (sql,parameters) = provider.GenerateQueryText(sqlQuery,baseAlias,baseTable,projectionColumns)

        (sql,parameters,projectionDelegate,baseTable)
