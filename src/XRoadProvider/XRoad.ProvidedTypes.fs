﻿namespace XRoad.ProvidedTypes

open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Quotations
open ProviderImplementation.ProvidedTypes
open System
open System.Collections.Generic
open System.Collections.Concurrent
open System.IO
open System.Reflection
open XRoad

/// Generated type providers for X-Road infrastructure.
/// Currently only one type provider is available, which builds service interface for certain producer.
[<TypeProvider>]
type XRoadProducerTypeProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces()
    
    let ns = "XRoad.Providers"
    let asm = Assembly.GetExecutingAssembly()
    let ctxt = ProvidedTypesContext.Create(config)
    
    let createProducerType () =
        let typeCache = ConcurrentDictionary<(string * string * string), ProvidedTypeDefinition>()

        let paramProducerType = ctxt.ProvidedTypeDefinition(asm, ns, "XRoadProducer", None, true, isErased = false)
        paramProducerType.AddXmlDoc("Type provider for generating service interfaces and data types for specific X-Road producer.")
        
        let producerUri = ctxt.ProvidedStaticParameter("ProducerUri", typeof<string>, "")
        producerUri.AddXmlDoc("WSDL document location (either local file or network resource).")
        
        let languageCode = ctxt.ProvidedStaticParameter("LanguageCode", typeof<string>, "et")
        languageCode.AddXmlDoc("Specify language code that is extracted as documentation tooltips. Default value is estonian (et).")

        paramProducerType.DefineStaticParameters([producerUri; languageCode], fun typeName args ->
            let arguments =
                args.[0] |> unbox<string>,
                args.[1] |> unbox<string>,
                typeName

            typeCache.GetOrAdd(arguments, fun args ->
                let targetType = ctxt.ProvidedTypeDefinition(asm, ns, typeName, None, true, isErased = false)
                ctxt |> XRoad.ProducerDefinition.makeProducerType args targetType
                )
        )
        
        paramProducerType

    do this.AddNamespace(ns, [createProducerType()])

/// Erased type providers for X-Road infrastructure.
/// Currently only one type provider is available, which acquires list of all producers from
/// security server.
[<TypeProvider>]
type XRoadProviders(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces()

    let theAssembly = typeof<XRoadProviders>.Assembly
    let namespaceName = "XRoad.Providers"
    let baseTy = typeof<obj>
    let ctx = ProvidedTypesContext.Create(config)

    // Main type which provides access to producer list.
    let serverTy =
        let typ = ctx.ProvidedTypeDefinition(theAssembly, namespaceName, "XRoadServer", Some baseTy)
        typ.AddXmlDoc("Type provider which discovers available producers from specified X-Road security server.")
        typ

    do
        let serverIPParam = ctx.ProvidedStaticParameter("ServerIP", typeof<string>)
        serverIPParam.AddXmlDoc("IP address of X-Road security server which is used for producer discovery task.")

        serverTy.DefineStaticParameters(
            [ serverIPParam ],
            fun typeName parameterValues ->
                let thisTy = ctx.ProvidedTypeDefinition(theAssembly, namespaceName, typeName, Some baseTy)
                match parameterValues with
                | [| :? string as serverIP |] ->
                    // Create field which holds default service endpoint for the security server.
                    let requestUri = ctx.ProvidedLiteralField("RequestUri", typeof<string>, sprintf "http://%s/cgi-bin/consumer_proxy" serverIP)
                    thisTy.AddMember(requestUri)
                    // Create type which holds producer list.
                    let producersTy = ctx.ProvidedTypeDefinition("Producers", Some baseTy, hideObjectMethods = true)
                    producersTy.AddXmlDoc("List of available database names registered at the security server.")
                    thisTy.AddMember(producersTy)
                    // Add list of members which each corresponds to certain producer.
                    SecurityServer.discoverProducers(serverIP)
                    |> List.map (fun producer ->
                        let producerTy = ctx.ProvidedTypeDefinition(producer.Name, Some baseTy, hideObjectMethods = true)
                        producerTy.AddMember(ctx.ProvidedLiteralField("ProducerName", typeof<string>, producer.Name))
                        producerTy.AddMember(ctx.ProvidedLiteralField("WsdlUri", typeof<string>, producer.WsdlUri))
                        producerTy.AddXmlDoc(producer.Description)
                        producerTy)
                    |> producersTy.AddMembers
                | _ -> failwith "Unexpected parameter values!"
                thisTy)

    let noteProperty message : MemberInfo =
        let property = ctx.ProvidedProperty("<Note>", typeof<string>, getterCode = (fun _ -> <@@ "" @@>), isStatic = true)
        property.AddXmlDoc(message)
        upcast property

    let buildServer6Types (typeName: string) (args: obj []) =
        let securityServerUri = Uri(unbox<string> args.[0])
        let xRoadInstance: string = unbox args.[1]
        let memberClass: string = unbox args.[2]
        let memberCode: string = unbox args.[3]
        let subsystemCode: string = unbox args.[4]
        let refresh: bool = unbox args.[5]

        let client =
            match subsystemCode with
            | null | "" -> SecurityServerV6.Member(xRoadInstance, memberClass, memberCode)
            | code -> SecurityServerV6.Subsystem(xRoadInstance, memberClass, memberCode, code)

        let thisTy = ctx.ProvidedTypeDefinition(theAssembly, namespaceName, typeName, Some baseTy)

        // Type which holds information about producers defined in selected instance.
        let producersTy = ctx.ProvidedTypeDefinition("Producers", Some baseTy, hideObjectMethods = true)
        producersTy.AddXmlDoc("All available producers in particular v6 X-Road instance.")
        thisTy.AddMember(producersTy)

        // Type which holds information about central services defined in selected instance.
        let centralServicesTy = ctx.ProvidedTypeDefinition("CentralServices", Some baseTy, hideObjectMethods = true)
        centralServicesTy.AddXmlDoc("All available central services in particular v6 X-Road instance.")
        thisTy.AddMember(centralServicesTy)

        producersTy.AddMembersDelayed (fun _ ->
            SecurityServerV6.downloadProducerList securityServerUri xRoadInstance refresh
            |> List.map (fun memberClass ->
                let classTy = ctx.ProvidedTypeDefinition(memberClass.Name, Some baseTy, hideObjectMethods = true)
                classTy.AddXmlDoc(memberClass.Name)
                classTy.AddMember(ctx.ProvidedLiteralField("ClassName", typeof<string>, memberClass.Name))
                classTy.AddMembersDelayed (fun () ->
                    memberClass.Members
                    |> List.map (fun memberItem ->
                        let memberId = SecurityServerV6.Member(xRoadInstance, memberClass.Name, memberItem.Code)
                        let addServices provider addNote =
                            try
                                let service: SecurityServerV6.Service = { Provider = provider; ServiceCode = "listMethods"; ServiceVersion = None }
                                match addNote, SecurityServerV6.downloadMethodsList securityServerUri client service with
                                | true, [] -> [noteProperty "No services are listed in this X-Road member."]
                                | _, ss -> ss |> List.map (fun x -> ctx.ProvidedLiteralField((sprintf "SERVICE:%s" x.ServiceCode), typeof<string>, Uri(securityServerUri, x.WsdlPath).ToString()) :> MemberInfo)
                            with e -> [noteProperty e.Message]
                        let memberTy = ctx.ProvidedTypeDefinition(sprintf "%s (%s)" memberItem.Name memberItem.Code, Some baseTy, hideObjectMethods = true)
                        memberTy.AddXmlDoc(memberItem.Name)
                        memberTy.AddMember(ctx.ProvidedLiteralField("Name", typeof<string>, memberItem.Name))
                        memberTy.AddMember(ctx.ProvidedLiteralField("Code", typeof<string>, memberItem.Code))
                        memberTy.AddMembersDelayed(fun _ -> addServices memberId false)
                        memberTy.AddMembersDelayed(fun () ->
                            memberItem.Subsystems
                            |> List.map (fun subsystem ->
                                let subsystemId = memberId.GetSubsystem(subsystem)
                                let subsystemTy = ctx.ProvidedTypeDefinition(sprintf "%s:%s" subsystemId.ObjectId subsystem, Some baseTy, hideObjectMethods = true)
                                subsystemTy.AddXmlDoc(sprintf "Subsystem %s of X-Road member %s (%s)." subsystem memberItem.Name memberItem.Code)
                                subsystemTy.AddMember(ctx.ProvidedLiteralField("Name", typeof<string>, subsystem))
                                subsystemTy.AddMembersDelayed(fun _ -> addServices subsystemId true)
                                subsystemTy))
                        memberTy))
                classTy))

        centralServicesTy.AddMembersDelayed (fun _ ->
            match SecurityServerV6.downloadCentralServiceList securityServerUri xRoadInstance refresh with
            | [] -> [noteProperty "No central services are listed in this X-Road instance."]
            | services -> services |> List.map (fun serviceCode -> upcast ctx.ProvidedLiteralField(serviceCode, typeof<string>, serviceCode)))

        thisTy

    let server6Parameters =
        [ ctx.ProvidedStaticParameter("SecurityServerUri", typeof<string>), "X-Road security server uri which is used to connect to that X-Road instance."
          ctx.ProvidedStaticParameter("XRoadInstance", typeof<string>), "Code identifying the instance of X-Road system."
          ctx.ProvidedStaticParameter("MemberClass", typeof<string>), "Member class that is used in client identifier in X-Road request."
          ctx.ProvidedStaticParameter("MemberCode", typeof<string>), "Member code that is used in client identifier in X-Road requests."
          ctx.ProvidedStaticParameter("SubsystemCode", typeof<string>, ""), "Subsystem code that is used in client identifier in X-Road requests."
          ctx.ProvidedStaticParameter("ForceRefresh", typeof<bool>, false), "When `true`, forces type provider to refresh data from security server." ]
        |> List.map (fun (parameter,doc) -> parameter.AddXmlDoc(doc); parameter)

    // Generic type for collecting information from selected X-Road instance.
    let server6Ty =
        let typ = ctx.ProvidedTypeDefinition(theAssembly, namespaceName, "XRoadServer6", Some baseTy)
        typ.AddXmlDoc("Type provider which collects data from selected X-Road instance.")
        typ

    do server6Ty.DefineStaticParameters(server6Parameters, buildServer6Types)
    do this.AddNamespace(namespaceName, [serverTy; server6Ty])
