﻿module private XRoad.DynamicMethods

open FSharp.Quotations
open FSharp.Quotations.DerivedPatterns
open FSharp.Quotations.Patterns
open System
open System.Collections.Concurrent
open System.Numerics
open System.Reflection
open System.Reflection.Emit
open System.Xml
open XRoad
open XRoad.Attributes

type DeserializerDelegate = delegate of XmlReader -> obj
type SerializerDelegate = delegate of XmlWriter * obj -> unit

type Deserialization =
    { Root: MethodInfo
      Content: MethodInfo
      MatchType: MethodInfo }

type TypeMap =
    { Type: Type
      Attribute: XRoadTypeAttribute
      DeserializeDelegate: Lazy<DeserializerDelegate>
      Deserialization: Deserialization
      SerializeDelegate: Lazy<SerializerDelegate>
      Serialization: MethodInfo }
    member this.Serialize(writer: XmlWriter, value: obj) =
        this.SerializeDelegate.Value.Invoke(writer, value)
    member this.Deserialize(reader: XmlReader) =
        this.DeserializeDelegate.Value.Invoke(reader)
    static member Create(typ, deserialization, serialization) =
        { Type = typ
          Attribute = typ.GetCustomAttribute<XRoadTypeAttribute>()
          Deserialization = deserialization
          DeserializeDelegate = lazy (deserialization.Root.CreateDelegate(typeof<DeserializerDelegate>) |> unbox)
          Serialization = serialization
          SerializeDelegate = lazy (serialization.CreateDelegate(typeof<SerializerDelegate>) |> unbox) }

let typeMaps = ConcurrentDictionary<Type, TypeMap>()

let objGetType = typeof<obj>.GetMethod("GetType", [| |])
let typeGetFullName = typeof<Type>.GetProperty("FullName").GetGetMethod()
let stringEquals = typeof<String>.GetMethod("Equals", [| typeof<string> |])

let generate (il: ILGenerator) (argFunc: Expr -> int option) (expr: Expr<'a>) =
    let rec genIL expr =
        let genArg (argsExpr: Expr list) =
            let rec genSingleArg (argExpr: Expr) =
                if argFunc argExpr |> Option.isNone then
                    match argExpr with
                    | Value(null, _) -> il.Emit(OpCodes.Ldnull)
                    | Value(value, typ) when typ = typeof<string> -> il.Emit(OpCodes.Ldstr, unbox<string> value)
                    | Coerce(e, _) -> genSingleArg e
                    | Call(_)
                    | PropertyGet(_)
                    | NewObject(_) -> genIL argExpr
                    | _ -> failwithf "Unimplemented expression: %A (%A)" argExpr expr
            argsExpr |> List.iter (genSingleArg)
        if argFunc expr |> Option.isNone then
            match expr with
            | Call(Some(instExpr), mi, argsExpr) ->
                genArg (instExpr :: argsExpr)
                il.Emit(OpCodes.Callvirt, mi)
                il.Emit(OpCodes.Nop)
            | SpecificCall <@@ ignore @@> (_, _, [argsExpr]) ->
                genIL argsExpr
                il.Emit(OpCodes.Pop)
            | SpecificCall <@@ raise @@> (_, _, [argsExpr]) ->
                genIL argsExpr
                il.Emit(OpCodes.Throw)
            | SpecificCall <@@ (=) @@> (_, _, argsExpr) ->
                genArg argsExpr
                il.Emit(OpCodes.Ceq)
                il.Emit(OpCodes.Ldc_I4_0)
                il.Emit(OpCodes.Ceq)
            | Call(None, mi, argsExpr) ->
                genArg argsExpr
                il.Emit(OpCodes.Call, mi)
            | IfThenElse(condExpr, trueExpr, falseExpr) ->
                let lbl1 = il.DefineLabel()
                let lbl2 = il.DefineLabel()
                genIL condExpr
                il.Emit(OpCodes.Brtrue_S, lbl1)
                genIL trueExpr
                il.Emit(OpCodes.Br, lbl2)
                il.MarkLabel(lbl1)
                il.Emit(OpCodes.Nop)
                genIL falseExpr
                il.MarkLabel(lbl2)
                il.Emit(OpCodes.Nop)
            | NewObject(ci, [argsExpr]) ->
                genIL argsExpr
                il.Emit(OpCodes.Newobj, ci)
            | Sequential(expr1, expr2) ->
                genIL expr1
                genIL expr2
            | PropertyGet(Some(targetExpr), pi, argsExpr) ->
                genArg (targetExpr::argsExpr)
                il.Emit(OpCodes.Callvirt, pi.GetGetMethod())
                il.Emit(OpCodes.Nop)
            | Value(null,_) ->
                il.Emit(OpCodes.Nop)
            | _ -> failwithf "Unimplemented expression: %A" expr
    genIL expr

let (!@) expr =
    match expr with
    | Call(_, mi, _) -> mi
    | PropertyGet(_, pi, _) -> pi.GetGetMethod()
    | _ -> failwithf "Must be method call expression, but was `%A`." expr

let (!!@) expr =
    match expr with
    | NewObject(ci, _) -> ci
    | _ -> failwith "Must be constructor expression"

let createMatchTypeMethod (typ: Type) = DynamicMethod(sprintf "%s_MatchType" typ.FullName, typeof<bool>, [| typeof<XmlReader> |])
let createSerializerMethod (typ: Type) skipVisibility = DynamicMethod(sprintf "%s_Serialize" typ.FullName, null, [| typeof<XmlWriter>; typeof<obj> |], (skipVisibility: bool))
let createDeserializerMethod (typ: Type) = DynamicMethod(sprintf "%s_Deserialize" typ.FullName, typeof<obj>, [| typeof<XmlReader> |])
let createDeserializeContentMethod (typ: Type) = DynamicMethod(sprintf "%s_DeserializeContent" typ.FullName, null, [| typeof<XmlReader>; typeof<obj>; typeof<bool> |])

let emitNullCheck (il: ILGenerator) (labelReturn: Label) =
    // var nilValue = (reader.GetAttribute("nil", XmlNamespace.Xsi) ?? "").ToLower();
    let nilValue = il.DeclareLocal(typeof<string>)
    let markSkipNull = il.DefineLabel()
    il.Emit(OpCodes.Ldarg_0)
    il.Emit(OpCodes.Ldstr, "nil")
    il.Emit(OpCodes.Ldstr, XmlNamespace.Xsi)
    il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlReader).GetAttribute("", "") @>)
    il.Emit(OpCodes.Dup)
    il.Emit(OpCodes.Brtrue_S, markSkipNull)
    il.Emit(OpCodes.Pop)
    il.Emit(OpCodes.Ldstr, "")
    il.MarkLabel(markSkipNull)
    il.Emit(OpCodes.Callvirt, !@ <@ "".ToLower() @>)
    il.Emit(OpCodes.Stloc, nilValue)

    // if (nilValue == "1" || nilValue == "true")
    let lbl1 = il.DefineLabel()
    let lbl2 = il.DefineLabel()
    let lbl3 = il.DefineLabel()
    il.Emit(OpCodes.Ldloc, nilValue)
    il.Emit(OpCodes.Ldstr, "1")
    il.Emit(OpCodes.Call, !@ <@ "" = "" @>)
    il.Emit(OpCodes.Brtrue_S, lbl1)
    il.Emit(OpCodes.Ldloc, nilValue)
    il.Emit(OpCodes.Ldstr, "true")
    il.Emit(OpCodes.Call, !@ <@ "" = "" @>)
    il.Emit(OpCodes.Ldc_I4_0)
    il.Emit(OpCodes.Ceq)
    il.Emit(OpCodes.Br_S, lbl2)
    il.MarkLabel(lbl1)
    il.Emit(OpCodes.Ldc_I4_0)
    il.MarkLabel(lbl2)
    il.Emit(OpCodes.Nop)
    il.Emit(OpCodes.Brtrue_S, lbl3)

    // return null;
    il.Emit(OpCodes.Ldnull)
    il.Emit(OpCodes.Br, labelReturn)
    il.MarkLabel(lbl3)

let createDeserializerMethodBody (il: ILGenerator) (typeMap: TypeMap) =
    if typeMap.Type.IsAbstract then
        // throw new Exception(string.Format("Cannot deserialize abstract type `{0}`.", typ.FullName));
        il.Emit(OpCodes.Ldstr, "Cannot deserialize abstract type `{0}`.")
        il.Emit(OpCodes.Ldstr, typeMap.Type.FullName)
        il.Emit(OpCodes.Call, !@ <@ String.Format("", "") @>)
        il.Emit(OpCodes.Newobj, typeof<Exception>.GetConstructor([| typeof<string> |]))
        il.Emit(OpCodes.Throw)
    else
        let markReturn = il.DefineLabel()

        emitNullCheck il markReturn

        // var instance = new T();
        let instance = il.DeclareLocal(typeMap.Type)
        il.Emit(OpCodes.Newobj, typeMap.Type.GetConstructor([| |]))
        il.Emit(OpCodes.Stloc, instance)

        // TODO: deserialize attributes

        // Deserialize content
        il.Emit(OpCodes.Ldarg_0)
        il.Emit(OpCodes.Ldloc, instance)
        il.Emit(OpCodes.Ldc_I4_0)
        il.Emit(OpCodes.Call, typeMap.Deserialization.Content)
        il.Emit(OpCodes.Nop)

        // return instance;
        il.Emit(OpCodes.Ldloc, instance)
        il.MarkLabel(markReturn)
        il.Emit(OpCodes.Ret)

let rec createDeserializeContentMethodBody (il: ILGenerator) (typeMaps: TypeMap list) (properties: (PropertyInfo * TypeMap) list) =
    let (|ContentProperty|_|) (properties: (PropertyInfo * TypeMap) list) =
        match properties with
        | [(prop,_) as x] ->
            match prop.GetCustomAttribute<XRoadContentAttribute>() with
            | null -> None
            | _ -> Some(x)
        | _ -> None

    let emitDeserialization (property: PropertyInfo) (propTypeMap: TypeMap) =
        let emitDefault (propTypeMap: TypeMap) =
            il.Emit(OpCodes.Ldarg_1)
            il.Emit(OpCodes.Castclass, typeMaps.Head.Type)
            il.Emit(OpCodes.Ldarg_0)
            il.Emit(OpCodes.Call, propTypeMap.Deserialization.Root)
            if propTypeMap.Type.IsValueType
            then il.Emit(OpCodes.Unbox_Any, propTypeMap.Type)
            else il.Emit(OpCodes.Castclass, propTypeMap.Type)
            il.Emit(OpCodes.Callvirt, property.GetSetMethod())
        match findSubTypes property.PropertyType with
        | [] -> emitDefault propTypeMap
        | subTypes ->
            let conditionEnd = il.DefineLabel()
            let selfLabel = il.DefineLabel()
            let strVar = il.DeclareLocal(typeof<string>)
            il.Emit(OpCodes.Ldarg_0)
            il.Emit(OpCodes.Ldstr, "type")
            il.Emit(OpCodes.Ldstr, XmlNamespace.Xsi)
            il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlReader).GetAttribute("", "") @>)
            il.Emit(OpCodes.Stloc, strVar)
            il.Emit(OpCodes.Ldloc, strVar)
            il.Emit(OpCodes.Brfalse, selfLabel)
            il.Emit(OpCodes.Ldloc, strVar)
            let chars = il.DeclareLocal(typeof<char[]>)
            il.Emit(OpCodes.Ldc_I4_1)
            il.Emit(OpCodes.Newarr, typeof<char>)
            il.Emit(OpCodes.Stloc, chars)
            il.Emit(OpCodes.Ldloc, chars)
            il.Emit(OpCodes.Ldc_I4_0)
            il.Emit(OpCodes.Ldc_I4, int32 ':')
            il.Emit(OpCodes.Stelem_I2)
            il.Emit(OpCodes.Ldloc, chars)
            il.Emit(OpCodes.Ldc_I4_2)
            let parts = il.DeclareLocal(typeof<string[]>)
            il.Emit(OpCodes.Callvirt, !@ <@ "".Split([| ':' |], 2) @>)
            il.Emit(OpCodes.Stloc, parts)
            let typeName = il.DeclareLocal(typeof<string>)
            let typeNamespace = il.DeclareLocal(typeof<string>)
            let label1 = il.DefineLabel()
            let label2 = il.DefineLabel()
            il.Emit(OpCodes.Ldloc, parts)
            il.Emit(OpCodes.Ldlen)
            il.Emit(OpCodes.Conv_I4)
            il.Emit(OpCodes.Ldc_I4_1)
            il.Emit(OpCodes.Ceq)
            il.Emit(OpCodes.Brtrue_S, label1)
            il.Emit(OpCodes.Ldloc, parts)
            il.Emit(OpCodes.Ldc_I4_1)
            il.Emit(OpCodes.Ldelem_Ref)
            il.Emit(OpCodes.Stloc, typeName)
            il.Emit(OpCodes.Ldarg_0)
            il.Emit(OpCodes.Ldloc, parts)
            il.Emit(OpCodes.Ldc_I4_0)
            il.Emit(OpCodes.Ldelem_Ref)
            il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlReader).LookupNamespace("") @>)
            il.Emit(OpCodes.Stloc, typeNamespace)
            il.Emit(OpCodes.Ldloc, typeNamespace)
            il.Emit(OpCodes.Brtrue_S, label2)
            il.Emit(OpCodes.Ldstr, "")
            il.Emit(OpCodes.Stloc, typeNamespace)
            il.Emit(OpCodes.Br_S, label2)
            il.MarkLabel(label1)
            il.Emit(OpCodes.Ldloc, parts)
            il.Emit(OpCodes.Ldc_I4_0)
            il.Emit(OpCodes.Ldelem_Ref)
            il.Emit(OpCodes.Stloc, typeName)
            il.Emit(OpCodes.Ldarg_0)
            il.Emit(OpCodes.Ldstr, "")
            il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlReader).LookupNamespace("") @>)
            il.Emit(OpCodes.Stloc, typeNamespace)
            il.MarkLabel(label2)
            il.Emit(OpCodes.Nop)

            let rec genSubType (lbl: Label option) subTypes =
                lbl |> Option.iter (fun lbl -> il.MarkLabel(lbl); il.Emit(OpCodes.Nop))
                match subTypes with
                | [] ->
                    il.Emit(OpCodes.Ldstr, "Invalid message: unknown type `{0}:{1}`.")
                    il.Emit(OpCodes.Ldloc, typeNamespace)
                    il.Emit(OpCodes.Ldloc, typeName)
                    il.Emit(OpCodes.Call, !@ <@ String.Format("", "", "") @>)
                    il.Emit(OpCodes.Newobj, typeof<Exception>.GetConstructor([| typeof<string> |]))
                    il.Emit(OpCodes.Throw)
                | x::xs ->
                    let lbl = il.DefineLabel()
                    il.Emit(OpCodes.Ldloc, typeName)
                    il.Emit(OpCodes.Ldstr, match x.Attribute.Name with null | "" -> x.Type.Name | x -> x)
                    il.Emit(OpCodes.Call, !@ <@ "" = "" @>)
                    il.Emit(OpCodes.Brfalse_S, lbl)
                    il.Emit(OpCodes.Ldloc, typeNamespace)
                    il.Emit(OpCodes.Ldstr, x.Attribute.Namespace)
                    il.Emit(OpCodes.Call, !@ <@ "" = "" @>)
                    il.Emit(OpCodes.Brfalse_S, lbl)
                    emitDefault x
                    il.Emit(OpCodes.Br, conditionEnd)
                    genSubType (Some lbl) xs
            subTypes |> genSubType None
            il.MarkLabel(selfLabel)
            emitDefault propTypeMap
            il.MarkLabel(conditionEnd)
            il.Emit(OpCodes.Nop)

    match properties with
    | ContentProperty(property,propTypeMap) ->
        emitDeserialization property propTypeMap
    | _ ->
        let varDepth = il.DeclareLocal(typeof<int>)
        il.Emit(OpCodes.Ldarg_0)
        il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlReader).Depth @>)
        il.Emit(OpCodes.Stloc, varDepth)

        let label = il.DefineLabel()
        let skipLabel = il.DefineLabel()
        il.Emit(OpCodes.Ldarg_2)
        il.Emit(OpCodes.Brfalse_S, label)
        il.Emit(OpCodes.Br, skipLabel)
        il.MarkLabel(label)
        il.Emit(OpCodes.Ldloc, varDepth)
        il.Emit(OpCodes.Ldc_I4_1)
        il.Emit(OpCodes.Add)
        il.Emit(OpCodes.Stloc, varDepth)

        typeMaps.Tail
        |> List.rev
        |> List.iter (fun typeMap ->
            il.Emit(OpCodes.Ldarg_0)
            il.Emit(OpCodes.Ldarg_1)
            il.Emit(OpCodes.Ldarg_2)
            il.Emit(OpCodes.Call, typeMap.Deserialization.Content)
            il.Emit(OpCodes.Nop))

        match typeMaps.Head.Attribute.Layout with
        | LayoutKind.Choice ->
            ()
        | LayoutKind.Sequence ->
            properties
            |> List.iteri (fun i (property, propTypeMap) ->
                let markLoopStart = il.DefineLabel()
                let markSuccess = il.DefineLabel()

                // reader.Read() -> false exception
                il.MarkLabel(markLoopStart)
                il.Emit(OpCodes.Ldarg_0)
                il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlReader).Read() @>)
                il.Emit(OpCodes.Brtrue_S, markSuccess)

                // throw new Exception("Invalid message: could not parse xml.");
                il.Emit(OpCodes.Ldstr, sprintf "Invalid message: expected `%s`, but was end of file." property.Name)
                il.Emit(OpCodes.Newobj, typeof<Exception>.GetConstructor([| typeof<string> |]))
                il.Emit(OpCodes.Throw)
                il.MarkLabel(markSuccess)
                il.Emit(OpCodes.Nop)

                if i = 0 then il.MarkLabel(skipLabel)

                let markSuccess2 = il.DefineLabel()
                il.Emit(OpCodes.Ldarg_0)
                il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlReader).NodeType @>)
                il.Emit(OpCodes.Ldc_I4, int32 XmlNodeType.EndElement)
                il.Emit(OpCodes.Ceq)
                il.Emit(OpCodes.Brfalse_S, markSuccess2)
                il.Emit(OpCodes.Ldarg_0)
                il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlReader).Depth @>)
                il.Emit(OpCodes.Ldloc, varDepth)
                il.Emit(OpCodes.Clt)
                il.Emit(OpCodes.Brfalse_S, markSuccess2)
                il.Emit(OpCodes.Ldstr, sprintf "Invalid message: expected `%s`, but was `</{0}>`." property.Name)
                il.Emit(OpCodes.Ldarg_0)
                il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlReader).LocalName @>)
                il.Emit(OpCodes.Call, !@ <@ String.Format("", "") @>)
                il.Emit(OpCodes.Newobj, typeof<Exception>.GetConstructor([| typeof<string> |]))
                il.Emit(OpCodes.Throw)
                il.MarkLabel(markSuccess2)
                il.Emit(OpCodes.Nop)

                // reader.Depth != depth
                il.Emit(OpCodes.Ldarg_0)
                il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlReader).Depth @>)
                il.Emit(OpCodes.Ldloc, varDepth)
                il.Emit(OpCodes.Ceq)
                il.Emit(OpCodes.Brfalse_S, markLoopStart)

                // reader.NodeType != XmlNodeType.Element
                il.Emit(OpCodes.Ldarg_0)
                il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlReader).NodeType @>)
                il.Emit(OpCodes.Ldc_I4_1)
                il.Emit(OpCodes.Ceq)
                il.Emit(OpCodes.Brfalse_S, markLoopStart)

                if propTypeMap.Attribute |> isNull || propTypeMap.Attribute.Layout <> LayoutKind.Choice then
                    // reader.LocalName != property.Name
                    let markDeserialize = il.DefineLabel()
                    il.Emit(OpCodes.Ldarg_0)
                    il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlReader).LocalName @>)
                    il.Emit(OpCodes.Ldstr, property.Name)
                    il.Emit(OpCodes.Call, !@ <@ "" = "" @>)
                    il.Emit(OpCodes.Brtrue_S, markDeserialize)
                    il.Emit(OpCodes.Ldstr, "Unexpected element: found `{0}`, but was expecting to find `{1}`.")
                    il.Emit(OpCodes.Ldarg_0)
                    il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlReader).LocalName @>)
                    il.Emit(OpCodes.Ldstr, property.Name)
                    il.Emit(OpCodes.Call, !@ <@ String.Format("", "", "") @>)
                    il.Emit(OpCodes.Newobj, typeof<Exception>.GetConstructor([| typeof<string> |]))
                    il.Emit(OpCodes.Throw)
                    il.MarkLabel(markDeserialize)

                // Deserialize property
                emitDeserialization property propTypeMap
                )
        | _ -> failwith "Not implemented"
    il.Emit(OpCodes.Ret)

and createTypeMap (typ: Type) =
    match typ.GetCustomAttribute<XRoadTypeAttribute>() with
    | null -> failwithf "Type `%s` is not serializable." typ.FullName
    | typeAttribute ->
        let deserializerMethod = createDeserializerMethod typ
        let deserializeContentMethod = createDeserializeContentMethod typ
        let serializerMethod = createSerializerMethod typ (typeAttribute.Layout = LayoutKind.Choice)
        let matchTypeMethod = createMatchTypeMethod typ
        let deserialization = { Root = deserializerMethod; Content = deserializeContentMethod; MatchType = matchTypeMethod }
        if typeMaps.TryAdd(typ, TypeMap.Create(typ, deserialization, serializerMethod)) then
            match typeAttribute.Layout with
            | LayoutKind.Choice ->
                createChoiceTypeSerializers (serializerMethod.GetILGenerator()) (deserializerMethod.GetILGenerator()) (deserializeContentMethod.GetILGenerator()) (matchTypeMethod.GetILGenerator()) typ
            | _ ->
                createSerializerMethodBody (serializerMethod.GetILGenerator()) typ
                createDeserializerMethodBody (deserializerMethod.GetILGenerator()) (getTypeMap typ)
                let properties =
                    typ.GetProperties(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.DeclaredOnly)
                    |> Array.filter (fun p -> p.GetCustomAttribute<XRoadElementAttribute>() |> isNull |> not ||
                                              p.GetCustomAttribute<XRoadContentAttribute>() |> isNull |> not)
                    |> Array.map (fun p -> (p, getTypeMap p.PropertyType))
                    |> Array.sortBy (fun (p,_) -> p.MetadataToken)
                    |> Array.toList
                createDeserializeContentMethodBody (deserializeContentMethod.GetILGenerator()) (findBaseTypes typ) properties
                createMatchType (matchTypeMethod.GetILGenerator()) (properties |> List.tryHead)
        typeMaps.[typ]

and createMatchType il property =
    match property with
    | Some(propertyInfo, propertyMap) ->
        il.Emit(OpCodes.Ldarg_0)
        if not (propertyMap.Attribute |> isNull) && propertyMap.Attribute.Layout = LayoutKind.Choice then
            il.Emit(OpCodes.Call, propertyMap.Deserialization.MatchType)
        else
            il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlReader).LocalName @>)
            il.Emit(OpCodes.Ldstr, propertyInfo.Name)
            il.Emit(OpCodes.Call, !@ <@ "" = "" @>)
    | None ->
        il.Emit(OpCodes.Ldc_I4_0)
    il.Emit(OpCodes.Ret)

and createChoiceTypeSerializers ilSer ilDeser ilDeserContent ilMatch choiceType =
    let idField =  match choiceType.GetField("__id", BindingFlags.Instance ||| BindingFlags.NonPublic) with
                   | null -> choiceType.GetField("__id@", BindingFlags.Instance ||| BindingFlags.NonPublic)
                   | x -> x
    let valueField = match choiceType.GetField("__value", BindingFlags.Instance ||| BindingFlags.NonPublic) with
                     | null -> choiceType.GetField("__value@", BindingFlags.Instance ||| BindingFlags.NonPublic)
                     | x -> x
    let conditionEnd = ilSer.DefineLabel()
    let rec genSerialization (label: Label option) (options: (XRoadChoiceOptionAttribute * Type * MethodInfo) list) =
        match options with
        | [] -> ()
        | (attr,typ,_)::xs ->
            label |> Option.iter (fun label -> ilSer.MarkLabel(label); ilSer.Emit(OpCodes.Nop))
            let label = match xs with [] -> conditionEnd | _ -> ilSer.DefineLabel()
            ilSer.Emit(OpCodes.Ldarg_1)
            ilSer.Emit(OpCodes.Castclass, choiceType)
            ilSer.Emit(OpCodes.Ldfld, idField)
            ilSer.Emit(OpCodes.Ldc_I4_S, attr.Id)
            ilSer.Emit(OpCodes.Ceq)
            ilSer.Emit(OpCodes.Brfalse, label)
            ilSer.Emit(OpCodes.Nop)
            if attr.IsElement then
                ilSer.Emit(OpCodes.Ldarg_0)
                ilSer.Emit(OpCodes.Ldstr, attr.Name)
                ilSer.Emit(OpCodes.Callvirt, !@ <@ (null: XmlWriter).WriteStartElement("") @>)
            ilSer.Emit(OpCodes.Ldarg_0)
            ilSer.Emit(OpCodes.Ldarg_1)
            ilSer.Emit(OpCodes.Castclass, choiceType)
            ilSer.Emit(OpCodes.Ldfld, valueField)
            ilSer.Emit(OpCodes.Call, (getTypeMap typ).Serialization)
            ilSer.Emit(OpCodes.Nop)
            ilSer.Emit(OpCodes.Br_S, conditionEnd)
            if attr.IsElement then
                ilSer.Emit(OpCodes.Ldarg_0)
                ilSer.Emit(OpCodes.Callvirt, !@ <@ (null: XmlWriter).WriteEndElement() @>)
            genSerialization (Some label) xs
    let genDeserialization options =
        let il = ilDeser
        let markReturn = il.DefineLabel()
        let rec generate (options: (XRoadChoiceOptionAttribute * Type * MethodInfo) list) =
            match options with
            | [] ->
                il.Emit(OpCodes.Ldnull)
                il.Emit(OpCodes.Br_S, markReturn)
            | (attr,typ,mi)::options ->
                let label = il.DefineLabel()
                let typeMap = getTypeMap typ
                if attr.IsElement then
                    il.Emit(OpCodes.Ldarg_0)
                    il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlReader).LocalName @>)
                    il.Emit(OpCodes.Ldstr, attr.Name)
                    il.Emit(OpCodes.Call, !@ <@ "" = "" @>)
                    il.Emit(OpCodes.Brfalse_S, label)
                    il.Emit(OpCodes.Ldarg_0)
                    il.Emit(OpCodes.Call, typeMap.Deserialization.Root)
                    if typeMap.Type.IsValueType
                    then il.Emit(OpCodes.Unbox_Any, typeMap.Type)
                    else il.Emit(OpCodes.Castclass, typeMap.Type)
                else
                    let instance = il.DeclareLocal(typeMap.Type)
                    il.Emit(OpCodes.Ldarg_0)
                    il.Emit(OpCodes.Call, typeMap.Deserialization.MatchType)
                    il.Emit(OpCodes.Brfalse_S, label)
                    il.Emit(OpCodes.Newobj, typeMap.Type.GetConstructor([| |]))
                    il.Emit(OpCodes.Stloc, instance)
                    il.Emit(OpCodes.Ldarg_0)
                    il.Emit(OpCodes.Ldloc, instance)
                    il.Emit(OpCodes.Ldc_I4_1)
                    il.Emit(OpCodes.Call, typeMap.Deserialization.Content)
                    il.Emit(OpCodes.Ldloc, instance)
                il.Emit(OpCodes.Call, mi)
                il.Emit(OpCodes.Br_S, markReturn)
                il.MarkLabel(label)
                il.Emit(OpCodes.Nop)
                generate options
        generate options
        il.MarkLabel(markReturn)
        il.Emit(OpCodes.Ret)
    let genMatch options =
        let il = ilMatch
        let markReturn = il.DefineLabel()
        let rec generate (options: (XRoadChoiceOptionAttribute * Type * MethodInfo) list) =
            match options with
            | [] ->
                il.Emit(OpCodes.Ldc_I4_0)
                il.Emit(OpCodes.Br_S, markReturn)
            | (attr,typ,_)::options ->
                let label = il.DefineLabel()
                let typeMap = getTypeMap typ
                if attr.IsElement then
                    il.Emit(OpCodes.Ldarg_0)
                    il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlReader).LocalName @>)
                    il.Emit(OpCodes.Ldstr, attr.Name)
                    il.Emit(OpCodes.Call, !@ <@ "" = "" @>)
                else
                    il.Emit(OpCodes.Ldarg_0)
                    il.Emit(OpCodes.Call, typeMap.Deserialization.MatchType)
                il.Emit(OpCodes.Brfalse_S, label)
                il.Emit(OpCodes.Ldc_I4_1)
                il.Emit(OpCodes.Br, markReturn)
                il.MarkLabel(label)
                il.Emit(OpCodes.Nop)
                generate options
        generate options
        il.MarkLabel(markReturn)
        il.Emit(OpCodes.Ret)
    choiceType.GetCustomAttributes<XRoadChoiceOptionAttribute>()
    |> Seq.map (fun attr ->
        let (typ, mi) =
            let methodName = sprintf "New%s%s" (if Char.IsLower(attr.Name.[0]) then "_" else "") attr.Name
            match choiceType.GetMethod(methodName, BindingFlags.Public ||| BindingFlags.Static) with
            | null -> failwithf "Type `%s` should define public static method `%s`." choiceType.FullName methodName
            | mi -> match mi.GetParameters() with
                    | [| pi |] -> (pi.ParameterType, mi)
                    | _ -> failwithf "Type `%s` method `New%s` should have exactly one argument." choiceType.FullName attr.Name
        (attr, typ, mi))
    |> Seq.toList
    |> (fun x -> genSerialization None x
                 genDeserialization x
                 genMatch x)
    ilSer.MarkLabel(conditionEnd)
    ilSer.Emit(OpCodes.Ret)
    ilDeserContent.Emit(OpCodes.Ret)

and emitSerializerParam (typeMap: TypeMap) (subTypes: TypeMap list) emitWriterParam emitValueParam (il: ILGenerator) =
    match subTypes with
    | [] ->
        emitWriterParam()
        emitValueParam()
        il.Emit(OpCodes.Call, typeMap.Serialization)
        il.Emit(OpCodes.Nop)
    | _ ->
        let conditionEnd = il.DefineLabel()
        let rec genSubType (lbl: Label option) subTypes =
            match subTypes with
            | [] -> ()
            | x::xs ->
                lbl |> Option.iter (fun lbl -> il.MarkLabel(lbl); il.Emit(OpCodes.Nop))
                let lbl = match xs with [] -> conditionEnd | _ -> il.DefineLabel()
                emitValueParam()
                il.Emit(OpCodes.Call, objGetType)
                il.Emit(OpCodes.Callvirt, typeGetFullName)
                il.Emit(OpCodes.Ldstr, x.Type.FullName)
                il.Emit(OpCodes.Callvirt, stringEquals)
                il.Emit(OpCodes.Ldc_I4_0)
                il.Emit(OpCodes.Ceq)
                il.Emit(OpCodes.Brtrue_S, lbl)
                il.Emit(OpCodes.Nop)
                emitWriterParam()
                emitValueParam()
                il.Emit(OpCodes.Call, x.Serialization)
                il.Emit(OpCodes.Nop)
                il.Emit(OpCodes.Nop)
                il.Emit(OpCodes.Br, conditionEnd)
                genSubType (Some lbl) xs
        subTypes |> genSubType None
        il.MarkLabel(conditionEnd)

and createSerializerMethodBody (il: ILGenerator) (typ: Type) =
    let writer: Expr<XmlWriter> = Expr.GlobalVar("w")
    let value: Expr<obj> = Expr.GlobalVar("v")

    generate il
             (fun arg -> match arg with
                         | e when e = (upcast value) -> il.Emit(OpCodes.Ldarg_1); Some(-1)
                         | e when e = (upcast writer) -> il.Emit(OpCodes.Ldarg_0); Some(-1)
                         | _ -> None)
             <@ if (%value) = null then (%writer).WriteAttributeString("nil", XmlNamespace.Xsi, "true") @>

    let emitPropertySerialization (il: ILGenerator) (property: PropertyInfo) =
        let contentAttribute = property.GetCustomAttribute<XRoadContentAttribute>() |> Option.ofObj
        let elementAttribute = property.GetCustomAttribute<XRoadElementAttribute>() |> Option.ofObj

        let isChoice = property.PropertyType.GetCustomAttribute<XRoadTypeAttribute>()
                       |> Option.ofObj
                       |> Option.fold (fun _ attr -> attr.Layout = LayoutKind.Choice) false

        let propertyName = property.Name
        let typeName = property.DeclaringType.FullName

        let serializer: Expr<unit> = Expr.GlobalVar("s")
        let attributes: Expr<unit> = Expr.GlobalVar("a")

        let contentExpr =
            if isChoice || (property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) |> isNull) then
                <@ (%serializer) @>
            elif elementAttribute |> Option.fold (fun _ x -> x.IsNullable) false then
                <@ (%attributes)
                   (%serializer) @>
            else
                <@ if (%value) = null then
                       raise (Exception(String.Format("Not nullable property `{0}` of type `{1}` has null value.", propertyName, typeName)))
                   else
                       (%attributes)
                       (%serializer) @>

        let expr =
            if contentAttribute.IsSome || isChoice then
                contentExpr
            else
                <@ (%writer).WriteStartElement(propertyName)
                   (%contentExpr)
                   (%writer).WriteEndElement() @>

        let typeMap = getTypeMap property.PropertyType
        let subTypes = findSubTypes property.PropertyType

        let emitWriterParam() =
            il.Emit(OpCodes.Ldarg_0)
        let emitValueParam() =
            il.Emit(OpCodes.Ldarg_1)
            il.Emit(OpCodes.Castclass, property.DeclaringType)
            il.Emit(OpCodes.Callvirt, property.GetGetMethod())
            if property.PropertyType.IsValueType then
                il.Emit(OpCodes.Box, property.PropertyType)
        let rec emitAttributesParam() =
            match subTypes with
            | [] -> ()
            | _ ->
                let conditionEnd = il.DefineLabel()
                let rec genSubType (lbl: Label option) subTypes =
                    match subTypes with
                    | [] -> ()
                    | x::xs ->
                        match lbl with Some(lbl) -> il.MarkLabel(lbl); il.Emit(OpCodes.Nop) | None -> ()
                        let lbl = match xs with [] -> conditionEnd | _ -> il.DefineLabel()
                        emitValueParam()
                        il.Emit(OpCodes.Call, objGetType)
                        il.Emit(OpCodes.Callvirt, typeGetFullName)
                        il.Emit(OpCodes.Ldstr, x.Type.FullName)
                        il.Emit(OpCodes.Callvirt, stringEquals)
                        il.Emit(OpCodes.Ldc_I4_0)
                        il.Emit(OpCodes.Ceq)
                        il.Emit(OpCodes.Brtrue_S, lbl)
                        il.Emit(OpCodes.Nop)
                        generate' <@ (%writer).WriteStartAttribute("type", XmlNamespace.Xsi) @>
                        let typeName = match x.Attribute.Name with null | "" -> x.Type.Name | name -> name
                        match x.Attribute.Namespace with
                        | null | "" -> generate' <@ (%writer).WriteString(typeName) @>
                        | ns -> generate' <@ (%writer).WriteQualifiedName(typeName, ns) @>
                        generate' <@ (%writer).WriteEndAttribute() @>
                        il.Emit(OpCodes.Br, conditionEnd)
                        genSubType (Some lbl) xs
                emitValueParam()
                il.Emit(OpCodes.Call, objGetType)
                il.Emit(OpCodes.Callvirt, typeGetFullName)
                il.Emit(OpCodes.Ldstr, property.PropertyType.FullName)
                il.Emit(OpCodes.Callvirt, stringEquals)
                il.Emit(OpCodes.Ldc_I4_0)
                il.Emit(OpCodes.Ceq)
                il.Emit(OpCodes.Brfalse, conditionEnd)
                il.Emit(OpCodes.Nop)
                subTypes |> genSubType None
                il.MarkLabel(conditionEnd)
        and generate' x = generate il (fun arg -> match arg with
                                                  | e when e = (upcast value) -> emitValueParam(); Some(-1)
                                                  | e when e = (upcast writer) -> emitWriterParam(); Some(-1)
                                                  | e when e = (upcast serializer) -> emitSerializerParam typeMap subTypes emitWriterParam emitValueParam il; Some(-1)
                                                  | e when e = (upcast attributes) -> emitAttributesParam(); Some(-1)
                                                  | _ -> None) x

        generate' expr
    let rec callBase (typ: Type) =
        match findTypeMap typ.BaseType with
        | Some(typeMap) ->
            callBase typ.BaseType
            il.Emit(OpCodes.Ldarg_0)
            il.Emit(OpCodes.Ldarg_1)
            il.Emit(OpCodes.Call, typeMap.Serialization)
            il.Emit(OpCodes.Nop)
        | _ -> ()
    callBase typ
    typ.GetProperties(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.DeclaredOnly)
    |> Array.choose (fun p ->
        if p.GetCustomAttribute<XRoadElementAttribute>() |> isNull && p.GetCustomAttribute<XRoadContentAttribute>() |> isNull
        then None
        else Some(p))
    |> Array.iter (emitPropertySerialization il)
    il.Emit(OpCodes.Ret)

and getTypeMap(typ) : TypeMap =
    match typeMaps.TryGetValue(typ) with
    | true, typeMap -> typeMap
    | false, _ -> createTypeMap typ

and findTypeMap (typ: Type) =
    match typ.GetCustomAttribute<XRoadTypeAttribute>() with
    | null -> None
    | _ -> Some(getTypeMap typ)

and findSubTypes (typ: Type) =
    let subTypes =
        typ.Assembly.GetTypes()
        |> Array.filter (fun x -> x.IsSubclassOf(typ))
        |> List.ofArray
    let rec orderTypes (ordered: Type list) (unordered: Type list) =
        let next, rem = unordered |> List.partition (fun x -> ordered |> List.exists ((=) x.BaseType))
        let newOrdered = next @ ordered
        match rem with
        | [] -> newOrdered
        | _ -> orderTypes newOrdered rem
    match subTypes with
    | [] -> []
    | xs -> orderTypes [typ] xs |> List.choose (findTypeMap) |> List.filter (fun x -> not x.Type.IsAbstract)

and findBaseTypes (typ: Type) =
    typ
    |> List.unfold (fun typ -> if typ = typeof<obj> then None else Some(findTypeMap typ, typ.BaseType))
    |> List.choose (id)

let createSystemTypeMap<'X> (writeMethods: MemberInfo list) (readMethods: MemberInfo list) =
    let createTypeMap isNullable =
        let typ = if isNullable then typedefof<Nullable<_>>.MakeGenericType(typeof<'X>) else typeof<'X>
        let serializerMethod =
            let meth = createSerializerMethod typ false
            let il = meth.GetILGenerator()
            let labelEnd =
                if isNullable || typ.IsClass then
                    let label = il.DefineLabel()
                    let labelEnd = il.DefineLabel()
                    il.Emit(OpCodes.Ldarg_1)
                    il.Emit(OpCodes.Ldnull)
                    il.Emit(OpCodes.Ceq)
                    il.Emit(OpCodes.Brfalse_S, label)
                    il.Emit(OpCodes.Ldarg_0)
                    il.Emit(OpCodes.Ldstr, "nil")
                    il.Emit(OpCodes.Ldstr, XmlNamespace.Xsi)
                    il.Emit(OpCodes.Ldstr, "true")
                    il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlWriter).WriteAttributeString("", "", "") @>)
                    il.Emit(OpCodes.Br_S, labelEnd)
                    il.MarkLabel(label)
                    Some(labelEnd)
                else None
            il.Emit(OpCodes.Ldarg_0)
            il.Emit(OpCodes.Ldarg_1)
            writeMethods
            |> List.iter (fun mi ->
                match mi with
                | :? MethodInfo as mi -> il.Emit(OpCodes.Callvirt, mi)
                | _ -> failwith "not implemented"
                il.Emit(OpCodes.Nop))
            il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlWriter).WriteValue(null: obj) @>)
            il.Emit(OpCodes.Nop)
            labelEnd |> Option.iter il.MarkLabel
            il.Emit(OpCodes.Ret)
            meth
        let deserializerMethod =
            let meth = createDeserializerMethod typ
            let il = meth.GetILGenerator()
            let labelRead = il.DefineLabel()
            let labelRet = il.DefineLabel()
            let labelCast = il.DefineLabel()
            if isNullable then
                emitNullCheck il labelRet
            il.Emit(OpCodes.Ldarg_0)
            il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlReader).IsEmptyElement @>)
            il.Emit(OpCodes.Brfalse_S, labelRead)
            il.Emit(OpCodes.Ldtoken, typeof<'X>)
            il.Emit(OpCodes.Call, !@ <@ Type.GetTypeFromHandle(RuntimeTypeHandle()) @>)
            il.Emit(OpCodes.Call, !@ <@ Activator.CreateInstance(typeof<int>) @>)
            il.Emit(OpCodes.Unbox_Any, typeof<'X>)
            il.Emit(OpCodes.Br_S, labelCast)
            il.MarkLabel(labelRead)
            il.Emit(OpCodes.Ldarg_0)
            il.Emit(OpCodes.Callvirt, !@ <@ (null: XmlReader).Read() @>)
            il.Emit(OpCodes.Pop)
            il.Emit(OpCodes.Ldarg_0)
            readMethods
            |> List.iter (fun mi ->
                match mi with
                | :? MethodInfo as mi -> il.Emit(OpCodes.Callvirt, mi)
                | :? ConstructorInfo as ci -> il.Emit(OpCodes.Newobj, ci)
                | _ -> failwith "not implemented")
            il.MarkLabel(labelCast)
            if isNullable then il.Emit(OpCodes.Newobj, typ.GetConstructor([| typeof<'X> |]))
            if typ.IsValueType then il.Emit(OpCodes.Box, typ)
            il.MarkLabel(labelRet)
            il.Emit(OpCodes.Ret)
            meth
        let deserialization = { Root = deserializerMethod; Content = null; MatchType = null }
        typeMaps.TryAdd(typ, TypeMap.Create(typ, deserialization, serializerMethod)) |> ignore
    if typeof<'X>.IsValueType then createTypeMap true
    createTypeMap false

do
    createSystemTypeMap<bool>
        []
        [!@ <@ (null: XmlReader).ReadContentAsBoolean() @>]
    createSystemTypeMap<int32>
        []
        [!@ <@ (null: XmlReader).ReadContentAsInt() @>]
    createSystemTypeMap<int64>
        []
        [!@ <@ (null: XmlReader).ReadContentAsLong() @>]
    createSystemTypeMap<BigInteger>
        [!@ <@ (null: obj).ToString() @>]
        [!@ <@ (null: XmlReader).ReadContentAsDecimal() @>; !!@ <@ BigInteger(1M) @>]
    createSystemTypeMap<DateTime>
        []
        [!@ <@ (null: XmlReader).ReadContentAsDateTime() @>]
    createSystemTypeMap<string>
        []
        [!@ <@ (null: XmlReader).ReadContentAsString() @>]
