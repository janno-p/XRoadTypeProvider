﻿namespace ProviderImplementation.ProvidedTypes

open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Quotations
open System
open System.Collections.Generic
open System.IO
open System.Reflection
open XRoad

/// Generated type providers for X-Road infrastructure.
/// Currently only one type provider is available, which builds service interface for certain producer.
[<TypeProvider>]
type XRoadProducerProvider() as this =
    let invalidation = Event<_,_>()

    let namespaceName = "XRoad.Providers"
    let theAssembly = typeof<XRoadProducerProvider>.Assembly

    // Already generated assemblies
    let typeCache = Dictionary<_,_>()

    // Available parameters to use for configuring type provider instance
    let staticParameters: ParameterInfo [] =
        let producerUriParam = ProvidedStaticParameter("ProducerUri", typeof<string>)
        producerUriParam.AddXmlDoc("WSDL document location (either local file or network resource).")
        let undescribedFaultsParam = ProvidedStaticParameter("UndescribedFaults", typeof<bool>, false)
        undescribedFaultsParam.AddXmlDoc("Generate code to handle service errors even if WSDL document doesn't explicitly define error responses.")
        let languageCodeParam = ProvidedStaticParameter("LanguageCode", typeof<string>, "et")
        languageCodeParam.AddXmlDoc("Specify language code that is extracted as documentation tooltips. Default value is estonian (et).")
        [| producerUriParam; undescribedFaultsParam; languageCodeParam |]

    interface ITypeProvider with
        /// Called when type alias is created, generates assembly for given arguments.
        override __.ApplyStaticArguments(typeWithoutArguments, typeNameWithArguments, staticArguments) =
            match typeWithoutArguments with
            | :? ProvidedTypeDefinition ->
                match staticArguments with
                | [| :? string as producerUri; :? bool as undescribedFaults; :? string as languageCode |] ->
                    // Same parameter set should have same output, so caching is reasonable.
                    let key = (String.Join(".", typeNameWithArguments), producerUri, undescribedFaults, languageCode)
                    match typeCache.TryGetValue(key) with
                    | false, _ ->
                        let typ = XRoad.ProducerDefinition.makeProducerType(typeNameWithArguments, producerUri, undescribedFaults, languageCode)
                        typeCache.Add(key, typ)
                        typ
                    | true, typ -> typ
                | _ -> failwith "invalid type provider arguments"
            | _ -> failwith "not implemented"

        /// Returns contents of assembly generated by type provider instance.
        override __.GetGeneratedAssemblyContents(assembly) =
            File.ReadAllBytes(assembly.ManifestModule.FullyQualifiedName)

        /// Generated types need to handle only instantiation and method call expressions, others are not used.
        override __.GetInvokerExpression(syntheticMethodBase, parameters) =
            let parameters = parameters |> List.ofArray
            match syntheticMethodBase with
            | :? ConstructorInfo as ctor -> Expr.NewObject(ctor, parameters)
            | :? MethodInfo as mi -> Expr.Call(parameters.Head, mi, parameters.Tail)
            | _ -> failwith "not implemented"

        /// Namespaces provided by this type provider instance.
        override __.GetNamespaces() = [| this |]

        /// Exactly one type can have static parameters with current implementation.
        override __.GetStaticParameters(typeWithoutArguments) =
            match typeWithoutArguments with
            | :? ProvidedTypeDefinition as ty when ty.Name = typeWithoutArguments.Name -> staticParameters
            | _ -> [| |]

        /// Default implementation for invalidation event.
        [<CLIEvent>]
        override __.Invalidate = invalidation.Publish

        /// No unmanaged resources to deallocate.
        override __.Dispose() = ()

    interface IProvidedNamespace with
        /// No nested namespaces defined.
        override __.GetNestedNamespaces() = [| |]

        /// Type provider contains exactly one abstract type which allows access to type provider functionality.
        override __.GetTypes() =
            let producerType = ProvidedTypeDefinition(theAssembly, namespaceName, "XRoadProducer", Some(typeof<obj>), IsErased=false)
            producerType.AddXmlDoc("Type provider for generating service interfaces and data types for specific X-Road producer.")
            [| producerType |]

        /// Use default namespace for type provider namespace.
        override __.NamespaceName with get() = namespaceName

        /// No types have to be resolved.
        override __.ResolveTypeName(_) = null

/// Erased type providers for X-Road infrastructure.
/// Currently only one type provider is available, which acquires list of all producers from
/// security server.
[<TypeProvider>]
type XRoadProviders() as this =
    inherit TypeProviderForNamespaces()

    let theAssembly = typeof<XRoadProviders>.Assembly
    let namespaceName = "XRoad.Providers"
    let baseTy = typeof<obj>

    // Main type which provides access to producer list.
    let serverTy =
        let typ = ProvidedTypeDefinition(theAssembly, namespaceName, "XRoadServer", Some baseTy)
        typ.AddXmlDoc("Type provider which discovers available producers from specified X-Road security server.")
        typ

    do
        let serverIPParam = ProvidedStaticParameter("ServerIP", typeof<string>)
        serverIPParam.AddXmlDoc("IP address of X-Road security server which is used for producer discovery task.")

        serverTy.DefineStaticParameters(
            [ serverIPParam ],
            fun typeName parameterValues ->
                let thisTy = ProvidedTypeDefinition(theAssembly, namespaceName, typeName, Some baseTy)
                match parameterValues with
                | [| :? string as serverIP |] ->
                    // Create field which holds default service endpoint for the security server.
                    let requestUri = ProvidedLiteralField("RequestUri", typeof<string>, sprintf "http://%s/cgi-bin/consumer_proxy" serverIP)
                    thisTy.AddMember(requestUri)
                    // Create type which holds producer list.
                    let producersTy = ProvidedTypeDefinition("Producers", Some baseTy, HideObjectMethods=true)
                    producersTy.AddXmlDoc("List of available database names registered at the security server.")
                    thisTy.AddMember(producersTy)
                    // Add list of members which each corresponds to certain producer.
                    SecurityServer.discoverProducers(serverIP)
                    |> List.map (fun producer ->
                        let producerTy = ProvidedTypeDefinition(producer.Name, Some baseTy, HideObjectMethods=true)
                        producerTy.AddMember(ProvidedLiteralField("ProducerName", typeof<string>, producer.Name))
                        producerTy.AddMember(ProvidedLiteralField("WsdlUri", typeof<string>, producer.WsdlUri))
                        producerTy.AddXmlDoc(producer.Description)
                        producerTy)
                    |> producersTy.AddMembers
                | _ -> failwith "Unexpected parameter values!"
                thisTy)

    let buildServer6Types (typeName: string) (args: obj []) =
        let securityServer: string = unbox args.[0]
        let xRoadInstance: string = unbox args.[1]
        let memberClass: string = unbox args.[2]
        let memberCode: string = unbox args.[3]
        let subsystemCode: string = unbox args.[4]
        let refresh: bool = unbox args.[5]
        let useHttps: bool = unbox args.[6]

        let client = match subsystemCode with
                     | null | "" -> SecurityServerV6.MemberId(memberClass, memberCode)
                     | code -> SecurityServerV6.SubsystemId(memberClass, memberCode, code)

        let thisTy = ProvidedTypeDefinition(theAssembly, namespaceName, typeName, Some baseTy)

        // Type which holds information about producers defined in selected instance.
        let producersTy = ProvidedTypeDefinition("Producers", Some baseTy, HideObjectMethods = true)
        producersTy.AddXmlDoc("All available producers in particular v6 X-Road instance.")
        thisTy.AddMember(producersTy)

        // Type which holds information about central services defined in selected instance.
        let centralServicesTy = ProvidedTypeDefinition("CentralServices", Some baseTy, HideObjectMethods = true)
        centralServicesTy.AddXmlDoc("All available central services in particular v6 X-Road instance.")
        thisTy.AddMember(centralServicesTy)

        producersTy.AddMembersDelayed (fun _ ->
            SecurityServerV6.downloadProducerList securityServer xRoadInstance refresh useHttps
            |> List.map (fun memberClass ->
                let classTy = ProvidedTypeDefinition(memberClass.Name, Some baseTy, HideObjectMethods = true)
                classTy.AddXmlDoc(memberClass.Name)
                classTy.AddMember(ProvidedLiteralField("ClassName", typeof<string>, memberClass.Name))
                classTy.AddMembersDelayed (fun () ->
                    memberClass.Members
                    |> List.map (fun memberItem -> 
                        let memberTy = ProvidedTypeDefinition(sprintf "%s (%s)" memberItem.Name memberItem.Code, Some baseTy, HideObjectMethods = true)
                        memberTy.AddXmlDoc(memberItem.Name)
                        memberTy.AddMember(ProvidedLiteralField("Name", typeof<string>, memberItem.Name))
                        memberTy.AddMember(ProvidedLiteralField("Code", typeof<string>, memberItem.Code))
                        let subsystemsTy = ProvidedTypeDefinition("Subsystems", Some baseTy, HideObjectMethods = true)
                        subsystemsTy.AddXmlDoc(sprintf "List of subsystems for %s." memberItem.Name)
                        subsystemsTy.AddMembersDelayed(fun () -> memberItem.Subsystems |> List.map (fun subsystem -> ProvidedLiteralField(subsystem, typeof<string>, subsystem)))
                        memberTy.AddMember(subsystemsTy)
                        let servicesTy = ProvidedTypeDefinition("Services", Some baseTy, HideObjectMethods = true)
                        servicesTy.AddMembersDelayed(fun _ ->
                            SecurityServerV6.downloadMethodsList securityServer xRoadInstance useHttps client (memberClass.Name, memberItem.Code)
                            |> List.map (fun x -> ProvidedLiteralField(x.ServiceCode, typeof<string>, x.ServiceCode)))
                        memberTy.AddMember(servicesTy)
                        memberTy))
                classTy))

        centralServicesTy.AddMembersDelayed (fun _ ->
            match SecurityServerV6.downloadCentralServiceList securityServer xRoadInstance refresh useHttps with
            | [] ->
                let property = ProvidedProperty("<Note>", typeof<string>, GetterCode = (fun _ -> <@@ "" @@>), IsStatic = true)
                property.AddXmlDoc("No central services are listed in this X-Road instance.")
                [property :> MemberInfo]
            | services ->
                services
                |> List.map (fun serviceCode ->
                    upcast ProvidedLiteralField(serviceCode, typeof<string>, serviceCode)))

        thisTy

    let server6Parameters =
        [ ProvidedStaticParameter("SecurityServer", typeof<string>), "Domain name or IP address of X-Road security server which is used to connect to that X-Road instance."
          ProvidedStaticParameter("XRoadInstance", typeof<string>), "Code identifying the instance of X-Road system."
          ProvidedStaticParameter("MemberClass", typeof<string>), "Member class that is used in client identifier in X-Road request."
          ProvidedStaticParameter("MemberCode", typeof<string>), "Member code that is used in client identifier in X-Road requests."
          ProvidedStaticParameter("SubsystemCode", typeof<string>, ""), "Subsystem code that is used in client identifier in X-Road requests."
          ProvidedStaticParameter("ForceRefresh", typeof<bool>, false), "When `true`, forces type provider to refresh data from security server."
          ProvidedStaticParameter("UseHttps", typeof<bool>, false), "When `true`, tryes to download data using https protocol." ]
        |> List.map (fun (parameter,doc) -> parameter.AddXmlDoc(doc); parameter)

    // Generic type for collecting information from selected X-Road instance.
    let server6Ty =
        let typ = ProvidedTypeDefinition(theAssembly, namespaceName, "XRoadServer6", Some baseTy)
        typ.AddXmlDoc("Type provider which collects data from selected X-Road instance.")
        typ

    do server6Ty.DefineStaticParameters(server6Parameters, buildServer6Types)
    do this.AddNamespace(namespaceName, [serverTy; server6Ty])
