﻿namespace XRoad

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Xml

[<RequireQualifiedAccessAttribute>]
module internal XmlNamespace =
    let [<Literal>] Http = "http://schemas.xmlsoap.org/soap/http"
    let [<Literal>] Mime = "http://schemas.xmlsoap.org/wsdl/mime/"
    let [<Literal>] Soap = "http://schemas.xmlsoap.org/wsdl/soap/"
    let [<Literal>] SoapEnc = "http://schemas.xmlsoap.org/soap/encoding/"
    let [<Literal>] SoapEnv = "http://schemas.xmlsoap.org/soap/envelope/"
    let [<Literal>] Wsdl = "http://schemas.xmlsoap.org/wsdl/"
    let [<Literal>] Xmime = "http://www.w3.org/2005/05/xmlmime"
    let [<Literal>] Xml = "http://www.w3.org/XML/1998/namespace"
    let [<Literal>] Xmlns = "http://www.w3.org/2000/xmlns/";
    let [<Literal>] Xop = "http://www.w3.org/2004/08/xop/include"
    let [<Literal>] XRoad20 = "http://x-tee.riik.ee/xsd/xtee.xsd"
    let [<Literal>] XRoad30 = "http://x-rd.net/xsd/xroad.xsd"
    let [<Literal>] XRoad31Ee = "http://x-road.ee/xsd/x-road.xsd"
    let [<Literal>] XRoad31Eu = "http://x-road.eu/xsd/x-road.xsd"
    let [<Literal>] XRoad40 = "http://x-road.eu/xsd/xroad.xsd"
    let [<Literal>] XRoad40Id = "http://x-road.eu/xsd/identifiers"
    let [<Literal>] XRoad40Repr = "http://xroad.eu/xsd/representation.xsd"
    let [<Literal>] Xsd = "http://www.w3.org/2001/XMLSchema"
    let [<Literal>] Xsi = "http://www.w3.org/2001/XMLSchema-instance"

    /// Defines namespaces which are handled separately (not generated).
    let predefined =
        [ Http; Mime; Soap; SoapEnc; SoapEnv; Wsdl; Xmime; Xml; Xmlns; Xop; Xsd; Xsi ]

type XRoadProtocol =
    | Undefined = 0
    | Version20 = 1
    | Version30 = 2
    | Version31 = 3
    | Version40 = 4

[<AutoOpen>]
module internal Option =
    /// Convert nullable value to option type.
    let ofObj o =
        if o = null then None else Some(o)

    /// Use default value in case of None.
    let orDefault value opt =
        opt |> Option.fold (fun _ t -> t) value

[<AutoOpen>]
module internal List =
    let tryHead lst = match lst with [] -> None | x::_ -> Some(x)

[<AutoOpen>]
module Commons =
    let isNull o = (o = null)

[<AutoOpen>]
module private XRoadProtocolExtensions =
    let protocolPrefix = function
        | XRoadProtocol.Version20 -> "xtee"
        | XRoadProtocol.Version30
        | XRoadProtocol.Version31 -> "xrd"
        | XRoadProtocol.Version40 -> failwith "Not implemented v4.0"
        | x -> failwithf "Invalid XRoadProtocol value `%A`" x

    let protocolNamespace = function
        | XRoadProtocol.Version20 -> XmlNamespace.XRoad20
        | XRoadProtocol.Version30 -> XmlNamespace.XRoad30
        | XRoadProtocol.Version31 -> XmlNamespace.XRoad31Ee
        | XRoadProtocol.Version40 -> failwith "Not implemented v4.0"
        | x -> failwithf "Invalid XRoadProtocol value `%A`" x

    /// Extracts X-Road protocol version from namespace that is used.
    let fromNamespace = function
        | XmlNamespace.XRoad20 -> XRoadProtocol.Version20
        | XmlNamespace.XRoad30 -> XRoadProtocol.Version30
        | XmlNamespace.XRoad31Ee -> XRoadProtocol.Version31
        | ns -> failwithf "Unexpected X-Road namespace value `%s`." ns

module XRoadHelper =
    let generateNonce() =
        let nonce = Array.create 42 0uy
        RNGCryptoServiceProvider.Create().GetNonZeroBytes(nonce)
        Convert.ToBase64String(nonce)

    let getSystemTypeName = function
        | "System.String" -> Some(XmlQualifiedName("string", XmlNamespace.Xsd))
        | "System.Boolean" -> Some(XmlQualifiedName("boolean", XmlNamespace.Xsd))
        | "System.DateTime" -> Some(XmlQualifiedName("dateTime", XmlNamespace.Xsd))
        | "System.Decimal" -> Some(XmlQualifiedName("decimal", XmlNamespace.Xsd))
        | "System.Double" -> Some(XmlQualifiedName("double", XmlNamespace.Xsd))
        | "System.Float" -> Some(XmlQualifiedName("float", XmlNamespace.Xsd))
        | "System.Int32" -> Some(XmlQualifiedName("int", XmlNamespace.Xsd))
        | "System.Numerics.BigInteger" -> Some(XmlQualifiedName("integer", XmlNamespace.Xsd))
        | "System.Int64" -> Some(XmlQualifiedName("long", XmlNamespace.Xsd))
        | _ -> None

    /// Define X-Road SOAP header element name and description values depending on operation style used in WSDL binding:
    /// First tuple contains RPC/Encoded style values and second one values for Document/Literal style.
    let headerMapping = function
        | "asutus"                    -> ("asutus", "Asutus", "Asutuse DNS-nimi."),
                                         ("consumer", "Consumer", "DNS-name of the institution")
        | "andmekogu"                 -> ("andmekogu", "Andmekogu", "Andmekogu DNS-nimi."),
                                         ("producer", "Producer", "DNS-name of the database")
        | "isikukood"                 -> ("isikukood", "Isikukood", "Teenuse kasutaja isikukood, millele eelneb kahekohaline maa kood. Näiteks EE37702026518."),
                                         ("userId", "UserId", "ID code of the person invoking the service, preceded by a two-letter country code. For example: EE37702026518")
        | "ametnik"                   -> ("ametnik", "Ametnik", "Teenuse kasutaja Eesti isikukood (ei ole kasutusel alates versioonist 5.0)."),
                                         ("", "", "")
        | "id"                        -> ("id", "Id", "Teenuse väljakutse nonss (unikaalne identifikaator)."),
                                         ("id", "Id", "Service invocation nonce (unique identifier)")
        | "nimi"                      -> ("nimi", "Nimi", "Kutsutava teenuse nimi."),
                                         ("service", "Service", "Name of the service to be invoked")
        | "toimik"                    -> ("toimik", "Toimik", "Teenuse väljakutsega seonduva toimiku number (mittekohustuslik)."),
                                         ("issue", "Issue", "Name of file or document related to the service invocation")
        | "allasutus"                 -> ("allasutus", "Allasutus", "Asutuse registrikood, mille nimel teenust kasutatakse (kasutusel juriidilise isiku portaalis)."),
                                         ("unit", "Unit", "Registration code of the institution or its unit on whose behalf the service is used (applied in the legal entity portal)")
        | "amet"                      -> ("amet", "Amet", "Teenuse kasutaja ametikoht."),
                                         ("position", "Position", "Organizational position or role of the person invoking the service")
        | "ametniknimi"               -> ("ametniknimi", "Ametniknimi", "Teenuse kasutaja nimi."),
                                         ("userName", "UserName", "Name of the person invoking the service")
        | "asynkroonne"               -> ("asynkroonne", "Asynkroonne", "Teenuse kasutamise asünkroonsus. Kui väärtus on 'true', siis sooritab turvaserver päringu asünkroonselt."),
                                         ("async", "Async", "Specifies asynchronous service. If the value is \"true\", then the security server performs the service call asynchronously.")
        | "autentija"                 -> ("autentija", "Autentija", "Teenuse kasutaja autentimise viis. Võimalikud variandid on: ID - ID-kaardiga autenditud; SERT - muu sertifikaadiga autenditud; PANK - panga kaudu autenditud; PAROOL - kasutajatunnuse ja parooliga autenditud. Autentimise viisi järel võib sulgudes olla täpsustus (näiteks panga kaudu autentimisel panga tunnus infosüsteemis)."),
                                         ("authenticator", "Authenticator", "Authentication method, one of the following: ID-CARD - with a certificate of identity; CERT - with another certificate; EXTERNAL - through a third-party service; PASSWORD - with user ID and a password. Details of the authentication (e.g. the identification of a bank for external authentication) can be given in brackets after the authentication method.")
        | "makstud"                   -> ("makstud", "Makstud", "Teenuse kasutamise eest makstud summa."),
                                         ("paid", "Paid", "The amount of money paid for invoking the service")
        | "salastada"                 -> ("salastada", "Salastada", "Kui asutusele on X-tee keskuse poolt antud päringute salastamise õigus ja andmekogu on nõus päringut salastama, siis selle elemendi olemasolul päringu päises andmekogu turvaserver krüpteerib päringu logi, kasutades selleks X-tee keskuse salastusvõtit."),
                                         ("encrypt", "Encrypt", "If an organization has got the right from the X-Road Center to hide queries, with the database agreeing to hide the query, the occurrence of this tag in the query header makes the database security server to encrypt the query log, using the encryption key of the X-Road Center")
        | "salastada_sertifikaadiga"  -> ("salastada_sertifikaadiga", "SalastadaSertifikaadiga", "Päringu sooritaja ID-kaardi autentimissertifikaat DERkujul base64 kodeerituna. Selle elemendi olemasolu päringu päises väljendab soovi päringu logi salastamiseks asutuse turvaserveris päringu sooritaja ID-kaardi autentimisvõtmega. Seda välja kasutatakse ainult kodaniku päringute portaalis."),
                                         ("encryptCert", "EncryptCert", "Authentication certificate of the query invokers ID Card, in the base64-encoded DER format. Occurrence of this tag in the query header represents the wish to encrypt the query log in the organizations security server, using authentication key of the query invokers ID Card. This field is used in the Citizen Query Portal only.")
        | "salastatud"                -> ("salastatud", "Salastatud", "Kui päringu välja päises oli element salastada ja päringulogi salastamine õnnestus, siis vastuse päisesse lisatakse tühi element salastatud."),
                                         ("encrypted", "Encrypted", "If the query header contains the encrypt tag and the query log as been successfully encrypted, an empty encrypted tag will be inserted in the reply header.")
        | "salastatud_sertifikaadiga" -> ("salastatud_sertifikaadiga", "SalastatudSertifikaadiga", "Kui päringu päises oli element salastada_sertifikaadiga ja päringulogi salastamine õnnestus, siis vastuse päisesesse lisatakse tühi element salastatud_sertifikaadiga."),
                                         ("encryptedCert", "EncryptedCert", "If the query header contains the encryptedCert tag and the query log has been successfully encrypted, an empty encryptedCert tag will accordingly be inserted in the reply header.")
        | name                        -> failwithf "Invalid header name '%s'" name

    // Helper functions to extract values from three-argument tuples.
    let fst3 (x, _, _) = x
    let snd3 (_, x, _) = x
    let trd3 (_, _, x) = x

type internal ContentType =
    | FileStorage of FileInfo
    | Data of byte[]

type public ContentEncoding =
    | Binary = 0
    | Base64 = 1

[<AllowNullLiteral>]
type public BinaryContent internal (contentID: string, content: ContentType) =
    member val ContentEncoding = ContentEncoding.Binary with get, set
    member __.ContentID
        with get() =
            match contentID with
            | null | "" -> XRoadHelper.generateNonce()
            | _ -> contentID
    member __.OpenStream() : Stream =
        match content with
        | FileStorage(file) -> upcast file.OpenRead()
        | Data(data) -> upcast new MemoryStream(data)
    member __.GetBytes() =
        match content with
        | FileStorage(file) -> File.ReadAllBytes(file.FullName)
        | Data(data) -> data
    static member Create(file) = BinaryContent("", FileStorage(file))
    static member Create(contentID, file) = BinaryContent(contentID, FileStorage(file))
    static member Create(data) = BinaryContent("", Data(data))
    static member Create(contentID, data) = BinaryContent(contentID, Data(data))

type SoapHeaderValue(name: XmlQualifiedName, value: obj, required: bool) =
    member val Name = name with get
    member val Value = value with get
    member val IsRequired = required with get

type XRoadMessage() =
    member val Header: SoapHeaderValue array = [||] with get, set
    member val Body: obj = null with get, set
    member val Attachments = Dictionary<string, BinaryContent>() with get, set
    member val Namespaces = List<string>() with get

type XRoadRequestOptions(uri: string, isEncoded: bool, isMultipart: bool, protocol: XRoadProtocol) =
    member val IsEncoded = isEncoded with get
    member val IsMultipart = isMultipart with get
    member val Protocol = protocol with get
    member val Uri = uri with get
    member val Accessor: XmlQualifiedName = null with get, set

type XRoadResponseOptions(isEncoded: bool, isMultipart: bool, protocol: XRoadProtocol, responseType: Type) =
    member val IsEncoded = isEncoded with get
    member val IsMultipart = isMultipart with get
    member val Protocol = protocol with get
    member val ResponseType = responseType with get
    member val Accessor: XmlQualifiedName = null with get, set
    member val ExpectUnexpected = false with get, set

[<AllowNullLiteral>]
type SerializerContext() =
    let attachments = Dictionary<string, BinaryContent>()
    member val IsMultipart = false with get, set
    member val Attachments = attachments with get
    member this.AddAttachments(attachments: IDictionary<_,_>) =
        match attachments with
        | null -> ()
        | _ -> attachments |> Seq.iter (fun kvp -> this.Attachments.Add(kvp.Key, kvp.Value))
    member __.GetAttachment(href: string) =
        if href.StartsWith("cid:") then
            let contentID = href.Substring(4)
            match attachments.TryGetValue(contentID) with
            | true, value -> value
            | _ -> failwithf "Multipart message doesn't contain content part with ID `%s`." contentID
        else failwithf "Invalid multipart content reference: `%s`." href

[<AbstractClass; Sealed>]
type BinaryContentHelper private () =
    static member DeserializeBinaryContent(reader: XmlReader, context: SerializerContext) =
        let nilValue = match reader.GetAttribute("nil", XmlNamespace.Xsi) with null -> "" | x -> x
        match nilValue.ToLower() with
        | "true" | "1" -> null
        | _ ->
            match reader.GetAttribute("href") with
            | null ->
                if reader.IsEmptyElement then BinaryContent.Create([| |])
                else
                    reader.Read() |> ignore
                    let bufferSize = 4096
                    let buffer = Array.zeroCreate<byte>(bufferSize)
                    use stream = new MemoryStream()
                    let rec readContents() =
                        let readCount = reader.ReadContentAsBase64(buffer, 0, bufferSize)
                        if readCount > 0 then stream.Write(buffer, 0, readCount)
                        if readCount = bufferSize then readContents()
                    readContents()
                    stream.Flush()
                    stream.Position <- 0L
                    BinaryContent.Create(stream.ToArray())
            | contentID -> context.GetAttachment(contentID)

    static member DeserializeXopBinaryContent(reader: XmlReader, context: SerializerContext) =
        let nilValue = match reader.GetAttribute("nil", XmlNamespace.Xsi) with null -> "" | x -> x
        match nilValue.ToLower() with
        | "true" | "1" -> null
        | _ ->
            if reader.IsEmptyElement then BinaryContent.Create([| |])
            else
                let depth = reader.Depth + 1
                let rec moveToXopInclude () =
                    if reader.Read() then
                        if reader.NodeType = XmlNodeType.EndElement && reader.Depth < depth then false
                        elif reader.NodeType <> XmlNodeType.Element || reader.Depth <> depth || reader.LocalName <> "Include" || reader.NamespaceURI <> XmlNamespace.Xop then moveToXopInclude()
                        else true
                    else false
                if moveToXopInclude () then
                    match reader.GetAttribute("href") with
                    | null -> failwithf "Missing reference to multipart content in xop:Include element."
                    | contentID -> context.GetAttachment(contentID)
                else BinaryContent.Create([| |])

    static member SerializeBinaryContent(writer: XmlWriter, value: obj, context: SerializerContext) =
        match value with
        | null -> writer.WriteAttributeString("nil", XmlNamespace.Xsi, "true")
        | _ ->
            let content = unbox<BinaryContent> value
            if context.IsMultipart then
                context.Attachments.Add(content.ContentID, content)
                writer.WriteAttributeString("href", sprintf "cid:%s" content.ContentID)
            else
                let bytes = (unbox<BinaryContent> value).GetBytes()
                writer.WriteBase64(bytes, 0, bytes.Length)

    static member SerializeXopBinaryContent(writer: XmlWriter, value: obj, context: SerializerContext) =
        match value with
        | null -> writer.WriteAttributeString("nil", XmlNamespace.Xsi, "true")
        | _ ->
            writer.WriteStartElement("xop", "Include", XmlNamespace.Xop)
            let content = unbox<BinaryContent> value
            context.Attachments.Add(content.ContentID, content)
            writer.WriteAttributeString("href", sprintf "cid:%s" content.ContentID)
            writer.WriteEndElement()

module internal Wsdl =
    open System.Xml.Linq

    /// Helper function for generating XNamespace-s.
    let xns name = XNamespace.Get(name)

    /// Helper function for generating XName-s.
    let xname name = XName.Get(name)

    /// Helper function for generating XName-s with namespace qualifier.
    let xnsname name ns = XName.Get(name, ns)

    let (%!) (e: XElement) (xn: XName) = e.Element(xn)
    let (%*) (e: XElement) (xn: XName) = e.Elements(xn)

    /// Active patterns for matching XML document nodes from various namespaces.
    [<AutoOpen>]
    module Pattern =
        /// Matches names defined in `http://www.w3.org/2001/XMLSchema` namespace.
        let (|XsdName|_|) (name: XName) =
            match name.NamespaceName with
            | XmlNamespace.Xsd -> Some name.LocalName
            | _ -> None

        /// Matches names defined in `http://www.w3.org/XML/1998/namespace` namespace.
        let (|XmlName|_|) (name: XName) =
            match name.NamespaceName with
            | "" | null | XmlNamespace.Xml -> Some name.LocalName
            | _ -> None

        /// Matches names defined in `http://x-road.ee/xsd/x-road.xsd` namespace.
        let (|XrdName|_|) (name: XName) =
            match name.NamespaceName with
            | XmlNamespace.XRoad31Ee -> Some name.LocalName
            | _ -> None

        /// Matches names defined in `http://x-tee.riik.ee/xsd/xtee.xsd` namespace.
        let (|XteeName|_|) (name: XName) =
            match name.NamespaceName with
            | XmlNamespace.XRoad20 -> Some name.LocalName
            | _ -> None

        /// Matches names defined in `http://schemas.xmlsoap.org/soap/encoding/` namespace.
        let (|SoapEncName|_|) (name: XName) =
            match name.NamespaceName with
            | XmlNamespace.SoapEnc -> Some name.LocalName
            | _ -> None

        /// Matches elements defined in `http://www.w3.org/2001/XMLSchema` namespace.
        let (|Xsd|_|) (element: XElement) =
            match element.Name with
            | XsdName name -> Some name
            | _ -> None

        /// Matches type names which are mapped to system types.
        let (|SystemType|_|) = function
            | XsdName "anyURI" -> Some typeof<string>
            | XsdName "boolean" -> Some typeof<bool>
            | XsdName "date" -> Some typeof<DateTime>
            | XsdName "dateTime" -> Some typeof<DateTime>
            | XsdName "decimal" -> Some typeof<decimal>
            | XsdName "double" -> Some typeof<double>
            | XsdName "float" -> Some typeof<single>
            | XsdName "int" -> Some typeof<int>
            | XsdName "integer" -> Some typeof<bigint>
            | XsdName "long" -> Some typeof<int64>
            | XsdName "string" -> Some typeof<string>
            | XsdName "ID" -> Some typeof<string>
            | XsdName "NMTOKEN" -> Some typeof<string>
            | XsdName name -> failwithf "Unmapped XSD type %s" name
            | SoapEncName name -> failwithf "Unmapped SOAP-ENC type %s" name
            | _ -> None

        /// Matches system types which can be serialized as MIME multipart attachments:
        /// From X-Road service protocol: if the message is encoded as MIME container then values of all scalar elements
        /// of the input with type of either `xsd:base64Binary` or `xsd:hexBinary` will be sent as attachments.
        let (|BinaryType|_|) = function
            | XsdName "hexBinary"
            | XsdName "base64Binary"
            | SoapEncName "base64Binary" -> Some typeof<byte[]>
            | _ -> None

        /// Matches X-Road legacy format header elements.
        let (|XteeHeader|_|) name =
            match name with
            | XteeName "asutus"
            | XteeName "andmekogu"
            | XteeName "isikukood"
            | XteeName "ametnik"
            | XteeName "id"
            | XteeName "nimi"
            | XteeName "toimik"
            | XteeName "allasutus"
            | XteeName "amet"
            | XteeName "ametniknimi"
            | XteeName "asynkroonne"
            | XteeName "autentija"
            | XteeName "makstud"
            | XteeName "salastada"
            | XteeName "salastada_sertifikaadiga"
            | XteeName "salastatud"
            | XteeName "salastatud_sertifikaadiga" -> Some(name)
            | _ -> None

        /// Matches X-Road header elements.
        let (|XRoadHeader|_|) name =
            match name with
            | XrdName "consumer"
            | XrdName "producer"
            | XrdName "userId"
            | XrdName "id"
            | XrdName "service"
            | XrdName "issue"
            | XrdName "unit"
            | XrdName "position"
            | XrdName "userName"
            | XrdName "async"
            | XrdName "authenticator"
            | XrdName "paid"
            | XrdName "encrypt"
            | XrdName "encryptCert"
            | XrdName "encrypted"
            | XrdName "encryptedCert" -> Some(name)
            | _ -> None

    /// Extracts optional attribute value from current element.
    /// Returns None if attribute is missing.
    let attr (name: XName) (element: XElement) =
        match element.Attribute(name) with
        | null -> None
        | attr -> Some attr.Value

    /// Extracts optional attribute value from current element.
    /// Return default value if attribute is missing.
    let attrOrDefault name value element =
        element |> attr name |> Option.orDefault value

    /// Extracts value of required attribute from current element.
    /// When attribute is not found, exception is thrown.
    let reqAttr (name: XName) (element: XElement) =
        match element.Attribute name with
        | null -> failwithf "Element %A attribute %A is required!" element.Name name
        | attr -> attr.Value

    /// Check if given node is constrained to use qualified form.
    /// Returns true if node requires qualified name.
    let isQualified attrName node =
        match node |> attrOrDefault attrName "unqualified" with
        | "qualified" -> true
        | "unqualified" -> false
        | x -> failwithf "Unknown %s value '%s'" attrName.LocalName x

    /// Parse qualified name from given string.
    let parseXName (element: XElement) (qualifiedName: string) =
        match qualifiedName.Split(':') with
        | [| name |] -> xnsname name <| element.GetDefaultNamespace().NamespaceName
        | [| prefix; name |] -> xnsname name <| element.GetNamespaceOfPrefix(prefix).NamespaceName
        | _ -> failwithf "Invalid qualified name string %s" qualifiedName

    /// Check if given uri is valid network location or file path in local file system.
    let resolveUri uri =
        match Uri.IsWellFormedUriString(uri, UriKind.Absolute) with
        | true -> uri
        | _ ->
            let fullPath = (new FileInfo(uri)).FullName
            match File.Exists(fullPath) with
            | true -> fullPath
            | _ -> failwith (sprintf "Cannot resolve url location `%s`" uri)

    /// Globally unique identifier for Xml Schema elements and types.
    type SchemaName =
        | SchemaElement of XName
        | SchemaType of XName
        member this.XName
            with get() =
                match this with
                | SchemaElement(name)
                | SchemaType(name) -> name
        override this.ToString() =
            match this with
            | SchemaElement(name) -> sprintf "SchemaElement(%A)" name
            | SchemaType(name) -> sprintf "SchemaType(%A)" name

    /// WSDL and SOAP binding style.
    type BindingStyle =
        | Document
        | Rpc
        static member FromNode(node, ?defValue) =
            match node |> attr (xname "style") with
            | Some("document") -> Document
            | Some("rpc") -> Rpc
            | Some(v) -> failwithf "Unknown binding style value `%s`" v
            | None -> defaultArg defValue Document

    /// Service method parameters for X-Road operations.
    type Parameter =
        { Name: XName
          Type: XName option }

    /// Combines parameter for request or response.
    type ParameterWrapper =
        { HasMultipartContent: bool
          Parameters: Parameter list
          RequiredHeaders: string list }

    /// Type that represents different style of message formats.
    type MethodCall =
        // Encoded always type
        | RpcEncodedCall of accessorName: XName * parameters: ParameterWrapper
        // Element directly under accessor element (named after message part name).
        // Type becomes the schema type of part accessor element.
        | RpcLiteralCall of accessorName: XName * parameters: ParameterWrapper
        // Encoded uses always type attribues.
        | DocEncodedCall of ns: XNamespace * parameters: ParameterWrapper
        // Element directly under body.
        // Type becomes the schema type of enclosing element (Body)
        | DocLiteralCall of parameters: ParameterWrapper
        member this.Accessor =
            match this with
            | DocEncodedCall(_) | DocLiteralCall(_) -> None
            | RpcEncodedCall(accessor, _) | RpcLiteralCall(accessor, _) -> Some(accessor)
        member this.Wrapper =
            match this with
            | DocEncodedCall(_, wrapper)
            | DocLiteralCall(wrapper)
            | RpcEncodedCall(_, wrapper)
            | RpcLiteralCall(_, wrapper) -> wrapper
        member this.IsEncoded =
            match this with RpcEncodedCall(_) -> true | _ -> false
        member this.RequiredHeaders =
            this.Wrapper.RequiredHeaders
        member this.IsMultipart =
            this.Wrapper.HasMultipartContent
        member this.Parameters =
            this.Wrapper.Parameters

    /// Definition for method which corresponds to single X-Road operation.
    type ServicePortMethod =
        { Name: string
          Version: string option
          InputParameters: MethodCall
          OutputParameters: MethodCall
          Documentation: string option }

    /// Collects multiple operations into logical group.
    type ServicePort =
        { Name: string
          Documentation: string option
          Uri: string
          Producer: string
          Methods: ServicePortMethod list
          Protocol: XRoadProtocol }

    /// All operations defined for single producer.
    type Service =
        { Name: string
          Ports: ServicePort list }


module internal MultipartMessage =
    open System.Net
    open System.Text

    type private ChunkState = Limit | NewLine | EndOfStream

    type private PeekStream(stream: Stream) =
        let mutable borrow = None : int option
        member __.Read() =
            match borrow with
            | Some(x) ->
                borrow <- None
                x
            | None -> stream.ReadByte()
        member __.Peek() =
            match borrow with
            | None ->
                let x = stream.ReadByte()
                borrow <- Some(x)
                x
            | Some(x) -> x
        member __.Flush() = stream.Flush()
        interface IDisposable with
            member __.Dispose() =
                stream.Dispose()

    let private getBoundaryMarker (response: WebResponse) =
        let parseMultipartContentType (contentType: string) =
            let parts = contentType.Split([| ';' |], StringSplitOptions.RemoveEmptyEntries)
                        |> List.ofArray
                        |> List.map (fun x -> x.Trim())
            match parts with
            | "multipart/related" :: parts ->
                parts |> List.tryFind (fun x -> x.StartsWith("boundary="))
                      |> Option.map (fun x -> x.Substring(9).Trim('"'))
            | _ -> None
        response
        |> Option.ofObj
        |> Option.map (fun r -> r.ContentType)
        |> Option.bind (parseMultipartContentType)

    let [<Literal>] private CHUNK_SIZE = 4096
    let [<Literal>] private CR = 13
    let [<Literal>] private LF = 10

    let private readChunkOrLine (buffer: byte []) (stream: PeekStream) =
        let rec addByte pos =
            if pos >= CHUNK_SIZE then (ChunkState.Limit, pos)
            else
                match stream.Read() with
                | -1 -> (ChunkState.EndOfStream, pos)
                | byt ->
                    if byt = CR && stream.Peek() = LF then
                        stream.Read() |> ignore
                        (ChunkState.NewLine, pos)
                    else
                        buffer.[pos] <- Convert.ToByte(byt)
                        addByte (pos + 1)
        let result = addByte 0
        stream.Flush()
        result

    let private readLine stream =
        let mutable line = [| |] : byte []
        let buffer = Array.zeroCreate<byte>(CHUNK_SIZE)
        let rec readChunk () =
            let (state, chunkSize) = stream |> readChunkOrLine buffer
            Array.Resize(&line, line.Length + chunkSize)
            Array.Copy(buffer, line, chunkSize)
            match state with
            | ChunkState.Limit -> readChunk()
            | ChunkState.EndOfStream
            | ChunkState.NewLine -> ()
        readChunk()
        line

    let private extractMultipartContentHeaders (stream: PeekStream) =
        let rec getHeaders () = seq {
            match Encoding.ASCII.GetString(stream |> readLine).Trim() with
            | null | "" -> ()
            | line ->
                match line.Split([| ':' |], 2) with
                | [| name |] -> yield (name.Trim(), "")
                | [| name; content |] -> yield (name.Trim(), content.Trim())
                | _ -> failwith "never"
                yield! getHeaders() }
        getHeaders() |> Map.ofSeq

    let private base64Decoder (encoding: Encoding) (encodedBytes: byte []) =
        match encodedBytes with
        | null | [| |] -> [| |]
        | _ ->
            let chars = encoding.GetChars(encodedBytes)
            Convert.FromBase64CharArray(chars, 0, chars.Length)

    let private getDecoder (contentEncoding: string) =
        match contentEncoding.ToLower() with
        | "base64" -> Some(base64Decoder)
        | "quoted-printable" | "7bit" | "8bit" | "binary" -> None
        | _ -> failwithf "No decoder implemented for content transfer encoding `%s`." contentEncoding

    let private startsWith (value: byte []) (buffer: byte []) =
        let rec compare i =
            if value.[i] = buffer.[i] then
                if i = 0 then true else compare (i - 1)
            else false
        if buffer |> isNull || value |> isNull || value.Length > buffer.Length then false
        else compare (value.Length - 1)

    let internal read (response: WebResponse) : Stream * BinaryContent list =
        match response |> getBoundaryMarker with
        | Some(boundaryMarker) ->
            use stream = new PeekStream(response.GetResponseStream())
            let contents = List<string option * MemoryStream>()
            let contentMarker = Encoding.ASCII.GetBytes(sprintf "--%s" boundaryMarker)
            let endMarker = Encoding.ASCII.GetBytes(sprintf "--%s--" boundaryMarker)
            let (|Content|End|Separator|) line =
                if line |> startsWith endMarker then End
                elif line |> startsWith contentMarker then Content
                else Separator
            let buffer = Array.zeroCreate<byte>(CHUNK_SIZE)
            let rec copyChunk addNewLine encoding (decoder: (Encoding -> byte[] -> byte[]) option) (contentStream: Stream) =
                let (state,size) = stream |> readChunkOrLine buffer
                if buffer |> startsWith endMarker then false
                elif buffer |> startsWith contentMarker then true
                elif state = ChunkState.EndOfStream then failwith "Unexpected end of multipart stream."
                else
                    if decoder.IsNone && addNewLine then contentStream.Write([| 13uy; 10uy |], 0, 2)
                    let (decodedBuffer,size) = decoder |> Option.fold (fun (buf,_) func -> let buf = buf |> func encoding
                                                                                           (buf,buf.Length)) (buffer,size)
                    contentStream.Write(decodedBuffer, 0, size)
                    match state with EndOfStream -> false | _ -> copyChunk (state = ChunkState.NewLine) encoding decoder contentStream
            let parseContentPart () =
                let headers = stream |> extractMultipartContentHeaders
                let contentId = headers |> Map.tryFind("content-id") |> Option.map (fun x -> x.Trim().Trim('<', '>'))
                let decoder = headers |> Map.tryFind("content-transfer-encoding") |> Option.bind (getDecoder)
                let contentStream = new MemoryStream()
                contents.Add(contentId, contentStream)
                copyChunk false Encoding.UTF8 decoder contentStream
            let rec parseContent () =
                match stream |> readLine with
                | Content -> if parseContentPart() then parseContent() else ()
                | End -> ()
                | Separator -> parseContent()
            parseContent()
            match contents |> Seq.toList with
            | (_,content)::attachments ->
                (upcast content, attachments
                                 |> List.map (fun (name,stream) ->
                                    use stream = stream
                                    stream.Position <- 0L
                                    BinaryContent.Create(name.Value, stream.ToArray())))
            | _ -> failwith "empty multipart content"
        | None ->
            use stream = response.GetResponseStream()
            let content = new MemoryStream()
            stream.CopyTo(content)
            (upcast content, [])
