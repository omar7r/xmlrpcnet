﻿/* 
XML-RPC.NET library
Copyright (c) 2001-2006, Charles Cook <charlescook@cookcomputing.com>

Permission is hereby granted, free of charge, to any person 
obtaining a copy of this software and associated documentation 
files (the "Software"), to deal in the Software without restriction, 
including without limitation the rights to use, copy, modify, merge, 
publish, distribute, sublicense, and/or sell copies of the Software, 
and to permit persons to whom the Software is furnished to do so, 
subject to the following conditions:

The above copyright notice and this permission notice shall be 
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, 
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES 
OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND 
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT 
HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, 
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, 
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
DEALINGS IN THE SOFTWARE.
*/

// TODO: overriding default mapping action in a struct should not affect nested structs

namespace CookComputing.XmlRpc
{
  using System;
  using System.Collections;
  using System.Globalization;
  using System.IO;
  using System.Reflection;
  using System.Text;
  using System.Text.RegularExpressions;
  using System.Threading;
  using System.Xml;
  using System.Diagnostics;
  using System.Collections.Generic;


  struct Fault
  {
    public int faultCode;
    public string faultString;
  }

  public class XmlRpcDeserializer
  {
    public XmlRpcNonStandard NonStandard
    {
      get { return m_nonStandard; }
      set { m_nonStandard = value; }
    }
    XmlRpcNonStandard m_nonStandard = XmlRpcNonStandard.None;

    // private properties
    bool AllowInvalidHTTPContent
    {
      get { return (m_nonStandard & XmlRpcNonStandard.AllowInvalidHTTPContent) != 0; }
    }

    bool AllowNonStandardDateTime
    {
      get { return (m_nonStandard & XmlRpcNonStandard.AllowNonStandardDateTime) != 0; }
    }

    bool AllowStringFaultCode
    {
      get { return (m_nonStandard & XmlRpcNonStandard.AllowStringFaultCode) != 0; }
    }

    bool IgnoreDuplicateMembers
    {
      get { return (m_nonStandard & XmlRpcNonStandard.IgnoreDuplicateMembers) != 0; }
    }

    bool MapEmptyDateTimeToMinValue
    {
      get { return (m_nonStandard & XmlRpcNonStandard.MapEmptyDateTimeToMinValue) != 0; }
    }

    bool MapZerosDateTimeToMinValue
    {
      get { return (m_nonStandard & XmlRpcNonStandard.MapZerosDateTimeToMinValue) != 0; }
    }

#if (!COMPACT_FRAMEWORK)
    public XmlRpcRequest DeserializeRequest(Stream stm, Type svcType)
    {
      if (stm == null)
        throw new ArgumentNullException("stm",
          "XmlRpcSerializer.DeserializeRequest");
      XmlReader xmlRdr = CreateXmlReader(stm);
      return DeserializeRequest(xmlRdr, svcType);
    }

    private static XmlReader CreateXmlReader(Stream stm)
    {
#if (!SILVERLIGHT)
      XmlTextReader xmlRdr = new XmlTextReader(stm);
      ConfigureXmlTextReader(xmlRdr);
      return xmlRdr;
#else
      XmlReader xmlRdr = XmlReader.Create(stm, ConfigureXmlReaderSettings());
      return xmlRdr;
#endif
    }

    private static XmlReader CreateXmlReader(TextReader txtrdr)
    {
#if (!SILVERLIGHT)
      XmlTextReader xmlRdr = new XmlTextReader(txtrdr);
      ConfigureXmlTextReader(xmlRdr);
      return xmlRdr;
#else
      XmlReader xmlRdr = XmlReader.Create(txtrdr, ConfigureXmlReaderSettings());
      return xmlRdr;
#endif
    }

#if (!SILVERLIGHT)
    private static void ConfigureXmlTextReader(XmlTextReader xmlRdr)
    {
      xmlRdr.Normalization = false;
      xmlRdr.ProhibitDtd = true;
      xmlRdr.WhitespaceHandling = WhitespaceHandling.All;
    }
#else
    private static XmlReaderSettings ConfigureXmlReaderSettings()
    {
      var settings = new XmlReaderSettings
      {
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = false,
      };
      return settings;
    }
#endif

    public XmlRpcRequest DeserializeRequest(TextReader txtrdr, Type svcType)
    {
      if (txtrdr == null)
        throw new ArgumentNullException("txtrdr",
          "XmlRpcSerializer.DeserializeRequest");
      XmlReader xmlRdr = CreateXmlReader(txtrdr);
      return DeserializeRequest(xmlRdr, svcType);
    }

    public XmlRpcRequest DeserializeRequest(XmlReader rdr, Type svcType)
    {
      try
      {
        XmlRpcRequest request = new XmlRpcRequest();
        IEnumerator<Node> iter = XmlRpcParser.ParseRequest(rdr).GetEnumerator();

        iter.MoveNext();
        string methodName = (iter.Current as MethodName).Name;
        request.method = methodName;

        request.mi = null;
        ParameterInfo[] pis = null;
        if (svcType != null)
        {
          // retrieve info for the method which handles this XML-RPC method
          XmlRpcServiceInfo svcInfo
            = XmlRpcServiceInfo.CreateServiceInfo(svcType);
          request.mi = svcInfo.GetMethodInfo(request.method);
          // if a service type has been specified and we cannot find the requested
          // method then we must throw an exception
          if (request.mi == null)
          {
            string msg = String.Format("unsupported method called: {0}",
                                        request.method);
            throw new XmlRpcUnsupportedMethodException(msg);
          }
          // method must be marked with XmlRpcMethod attribute
          Attribute attr = Attribute.GetCustomAttribute(request.mi,
            typeof(XmlRpcMethodAttribute));
          if (attr == null)
          {
            throw new XmlRpcMethodAttributeException(
              "Method must be marked with the XmlRpcMethod attribute.");
          }
          pis = request.mi.GetParameters();
        }

        bool gotParams = iter.MoveNext();
        if (!gotParams)
        {
          if (svcType != null)
          {
            if (pis.Length == 0)
            {
              request.args = new object[0];
              return request;
            }
            else
            {
              throw new XmlRpcInvalidParametersException(
                "Method takes parameters and params element is missing.");
            }
          }
          else
          {
            request.args = new object[0];
            return request;
          }
        }

        int paramsPos = pis != null ? GetParamsPos(pis) : -1;
        Type paramsType = null;
        if (paramsPos != -1)
          paramsType = pis[paramsPos].ParameterType.GetElementType();
        int minParamCount = pis == null ? int.MaxValue 
          : (paramsPos == -1 ? pis.Length : paramsPos);
        ParseStack parseStack = new ParseStack("request");
        MappingAction mappingAction = MappingAction.Error;
        var objs = new List<object>();
        var paramsObjs = new List<object>();
        int paramCount = 0;


        while (iter.MoveNext())
        {
          paramCount++;
          if (svcType != null && paramCount > minParamCount && paramsPos == -1)
            throw new XmlRpcInvalidParametersException(
              "Request contains too many param elements based on method signature.");
          if (paramCount <= minParamCount)
          {
            if (svcType != null)
            {
              parseStack.Push(String.Format("parameter {0}", paramCount));
              // TODO: why following commented out?
              //          parseStack.Push(String.Format("parameter {0} mapped to type {1}", 
              //            i, pis[i].ParameterType.Name));
              var obj = ParseValueNode(iter,
                pis[paramCount - 1].ParameterType, parseStack, mappingAction);
              objs.Add(obj);
            }
            else
            {
              parseStack.Push(String.Format("parameter {0}", paramCount));
              var obj = ParseValueNode(iter, null, parseStack, mappingAction);
              objs.Add(obj);
            }
            parseStack.Pop();
          }
          else
          {
            parseStack.Push(String.Format("parameter {0}", paramCount + 1));
            var paramsObj = ParseValueNode(iter, paramsType, parseStack, mappingAction);
            paramsObjs.Add(paramsObj);
            parseStack.Pop();
          }
        }

        if (svcType != null && paramCount < minParamCount)
          throw new XmlRpcInvalidParametersException(
            "Request contains too few param elements based on method signature.");

        if (paramsPos != -1)
        {
          Object[] args = new Object[1];
          args[0] = paramCount - minParamCount;
          Array varargs = (Array)CreateArrayInstance(pis[paramsPos].ParameterType,
            args);
          for (int i = 0; i < paramsObjs.Count; i++)
            varargs.SetValue(paramsObjs[i], i);
          objs.Add(varargs);
        }
        request.args = objs.ToArray();
        return request;
      }
      catch (XmlException ex)
      {
        throw new XmlRpcIllFormedXmlException("Request contains invalid XML", ex);
      }
    }

    int GetParamsPos(ParameterInfo[] pis)
    {
      if (pis.Length == 0)
        return -1;
      if (Attribute.IsDefined(pis[pis.Length - 1], typeof(ParamArrayAttribute)))
      {
        return pis.Length - 1;
      }
      else
        return -1;
    }
#endif

    public XmlRpcResponse DeserializeResponse(Stream stm, Type svcType)
    {
      if (stm == null)
        throw new ArgumentNullException("stm",
          "XmlRpcSerializer.DeserializeResponse");
      if (AllowInvalidHTTPContent)
      {
        Stream newStm = new MemoryStream();
        Util.CopyStream(stm, newStm);
        stm = newStm;
        stm.Position = 0;
        while (true)
        {
          // for now just strip off any leading CR-LF characters
          int byt = stm.ReadByte();
          if (byt == -1)
            throw new XmlRpcIllFormedXmlException(
              "Response from server does not contain valid XML.");
          if (byt != 0x0d && byt != 0x0a && byt != ' ' && byt != '\t')
          {
            stm.Position = stm.Position - 1;
            break;
          }
        }
      }
      var settings = new XmlReaderSettings
      {
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = false,
      };
      XmlReader xmlRdr = XmlReader.Create(stm, settings);
      return DeserializeResponse(xmlRdr, svcType);
    }

    public XmlRpcResponse DeserializeResponse(TextReader txtrdr, Type svcType)
    {
      if (txtrdr == null)
        throw new ArgumentNullException("txtrdr",
          "XmlRpcSerializer.DeserializeResponse");
      var settings = new XmlReaderSettings
      {
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = false,
      };
      XmlReader xmlRdr = XmlReader.Create(txtrdr, settings);
      return DeserializeResponse(xmlRdr, svcType);
    }

    public XmlRpcResponse DeserializeResponse(XmlReader rdr, Type returnType)
    {
      try
      {

        IEnumerator<Node> iter = XmlRpcParser.ParseResponse(rdr).GetEnumerator();
        iter.MoveNext();
        if (iter.Current is FaultNode)
        {
          var xmlRpcException = DeserializeFault(iter);
          throw xmlRpcException;
        }
        if (returnType == typeof(void) || !iter.MoveNext())
          return new XmlRpcResponse { retVal = null }; 
        var valueNode = iter.Current as ValueNode;
        object retObj = ParseValueNode(iter, returnType, new ParseStack("response"),
          MappingAction.Error);
        var response = new XmlRpcResponse { retVal = retObj };
        return response;
      }
      catch (XmlException ex)
      {
        throw new XmlRpcIllFormedXmlException("Response contains invalid XML", ex);
      }
    }

    private XmlRpcException DeserializeFault(IEnumerator<Node> iter)
    {
      ParseStack faultStack = new ParseStack("fault response");
      // TODO: use global action setting
      MappingAction mappingAction = MappingAction.Error;
      XmlRpcFaultException faultEx = ParseFault(iter, faultStack, // TODO: fix
        mappingAction);
      throw faultEx;
    }

    public Object ParseValueNode(
      IEnumerator<Node> iter,
      Type valType,
      ParseStack parseStack,
      MappingAction mappingAction)
    {
      var valueNode = iter.Current as ValueNode;
      // if suppplied type is System.Object then ignore it because
      // if doesn't provide any useful information (parsing methods
      // expect null in this case)
      if (valType != null && valType.BaseType == null)
        valType = null;
      object ret = "";

      if (valueNode is StringValue && valueNode.ImplicitValue)
        CheckImplictString(valType, parseStack);

      Type parsedType;

      object retObj = null;
      if (iter.Current is ArrayValue)
        retObj = ParseArray(iter, valType, parseStack, mappingAction,
          out parsedType);
      else if (iter.Current is StructValue)
      {
        // if we don't know the expected struct type then we must
        // parse the XML-RPC struct as an instance of XmlRpcStruct
        if (valType != null && valType != typeof(XmlRpcStruct)
          && !valType.IsSubclassOf(typeof(XmlRpcStruct)))
        {
          retObj = ParseStruct(iter, valType, parseStack, mappingAction,
            out parsedType);
        }
        else
        {
          if (valType == null || valType == typeof(object))
            valType = typeof(XmlRpcStruct);
          // TODO: do we need to validate type here?
          retObj = ParseHashtable(iter, valType, parseStack, mappingAction,
            out parsedType);
        }
      }
      else if (iter.Current is Base64Value)
        retObj = ParseBase64(valueNode.Value, valType, parseStack, mappingAction,
          out parsedType);
      else if (iter.Current is IntValue)
      {
        retObj = ParseInt(valueNode.Value, valType, parseStack, mappingAction,
          out parsedType);
      }
      else if (iter.Current is LongValue)
      {
        retObj = ParseLong(valueNode.Value, valType, parseStack, mappingAction,
          out parsedType);
      }
      else if (iter.Current is StringValue)
      {
        retObj = ParseString(valueNode.Value, valType, parseStack, mappingAction,
          out parsedType);
      }
      else if (iter.Current is BooleanValue)
      {
        retObj = ParseBoolean(valueNode.Value, valType, parseStack, mappingAction,
          out parsedType);
      }
      else if (iter.Current is DoubleValue)
      {
        retObj = ParseDouble(valueNode.Value, valType, parseStack, mappingAction,
          out parsedType);
      }
      else if (iter.Current is DateTimeValue)
      {
        retObj = ParseDateTime(valueNode.Value, valType, parseStack, mappingAction,
          out parsedType);
      }
      else if (iter.Current is NilValue)
      {
        retObj = ParseNilValue(valueNode.Value, valType, parseStack, mappingAction,
          out parsedType);
      }

      return retObj;
    }

    private object ParseDateTime(string value, Type valType, ParseStack parseStack, MappingAction mappingAction, out Type parsedType)
    {
      CheckExpectedType(valType, typeof(DateTime), parseStack);
      parsedType = typeof(DateTime);
      return OnStack("dateTime", parseStack, delegate()
      {
        if (value == "" && MapEmptyDateTimeToMinValue)
          return DateTime.MinValue;
        DateTime retVal;
        if (!DateTime8601.TryParseDateTime8601(value, out retVal))
        {
          if (MapZerosDateTimeToMinValue && value.StartsWith("0000")
            && (value == "00000000T00:00:00" || value == "0000-00-00T00:00:00Z"
            || value == "00000000T00:00:00Z" || value == "0000-00-00T00:00:00"))
            retVal = DateTime.MinValue;
          else
            throw new XmlRpcInvalidXmlRpcException(parseStack.ParseType
              + " contains invalid dateTime value "
              + StackDump(parseStack));
        }
        return retVal;
      });
    }

    private object ParseDouble(string value, Type valType, ParseStack parseStack, MappingAction mappingAction, out Type parsedType)
    {
      CheckExpectedType(valType, typeof(double), parseStack);
      parsedType = typeof(double);
      return OnStack("double", parseStack, delegate()
      {
        try
        {
          double ret = Double.Parse(value, CultureInfo.InvariantCulture.NumberFormat);
          return ret;
        }
        catch (Exception)
        {
          throw new XmlRpcInvalidXmlRpcException(parseStack.ParseType
            + " contains invalid double value " + StackDump(parseStack));
        }
      });
    }

    private object ParseBoolean(string value, Type valType, ParseStack parseStack, MappingAction mappingAction, out Type parsedType)
    {
      CheckExpectedType(valType, typeof(bool), parseStack);
      parsedType = typeof(bool);
      return OnStack("boolean", parseStack, delegate()
      {
        if (value == "1")
          return true;
        else if (value == "0")
          return false;
        else
          throw new XmlRpcInvalidXmlRpcException(parseStack.ParseType
            + " contains invalid boolean value "
            + StackDump(parseStack));
      });
    }

    private object ParseString(string value, Type valType, ParseStack parseStack, MappingAction mappingAction, out Type parsedType)
    {
      CheckExpectedType(valType, typeof(string), parseStack);
      parsedType = typeof(string);
      return OnStack("string", parseStack, delegate()
      { return value; });
    }

    private object ParseLong(string value, Type valType, ParseStack parseStack, MappingAction mappingAction, out Type parsedType)
    {
      CheckExpectedType(valType, typeof(long), parseStack);
      parsedType = typeof(long);
      return OnStack("i8", parseStack, delegate()
      {
        long ret;
        if (!Int64.TryParse(value, out ret))
          throw new XmlRpcInvalidXmlRpcException(parseStack.ParseType
            + " contains invalid i8 value " + StackDump(parseStack));
        return ret;
      });
    }

    private object ParseInt(string value, Type valType, ParseStack parseStack, MappingAction mappingAction, out Type parsedType)
    {
      CheckExpectedType(valType, typeof(int), parseStack);
      parsedType = typeof(int);
      return OnStack("integer", parseStack, delegate()
      { 
        int ret;
        if (!Int32.TryParse(value, out ret))
          throw new XmlRpcInvalidXmlRpcException(parseStack.ParseType
            + " contains invalid int value " + StackDump(parseStack));
        return ret;
      });
    }

    private object ParseBase64(string value, Type valType, ParseStack parseStack, MappingAction mappingAction, out Type parsedType)
    {
      CheckExpectedType(valType, typeof(byte[]), parseStack);
      parsedType = typeof(int);
      return OnStack("base64", parseStack, delegate()
      { 
        if (value == "")
          return new byte[0];
        else
        {
          try
          {
            byte[] ret = Convert.FromBase64String(value);
            return ret;
          }
          catch (Exception)
          {
            throw new XmlRpcInvalidXmlRpcException(parseStack.ParseType
              + " contains invalid base64 value "
              + StackDump(parseStack));
          }
        }
      });
    }

    private object ParseNilValue(string p, Type type, ParseStack parseStack, MappingAction mappingAction, out Type parsedType)
    {
      if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
      {
        parsedType = type;
        return null;
      }
      else if (!type.IsPrimitive || !type.IsValueType)
      {
        parsedType = type;
        return null;
      }
      else
      {
        parsedType = null;
        throw new NotImplementedException(); // TODO: fix
      }
    }

    private object ParseHashtable(IEnumerator<Node> iter, Type valType, ParseStack parseStack, MappingAction mappingAction, out Type parsedType)
    {
      parsedType = null;
      XmlRpcStruct retObj = new XmlRpcStruct();
      parseStack.Push("struct mapped to XmlRpcStruct");
      try
      {
        while (iter.MoveNext() && iter.Current is StructMember)
        {
          string rpcName = (iter.Current as StructMember).Value;
          if (retObj.ContainsKey(rpcName)
            && !IgnoreDuplicateMembers)
            throw new XmlRpcInvalidXmlRpcException(parseStack.ParseType
              + " contains struct value with duplicate member "
              + rpcName
              + " " + StackDump(parseStack));
          iter.MoveNext();

          object value = OnStack(String.Format("member {0}", rpcName),
            parseStack, delegate()
            {
              return ParseValueNode(iter, null, parseStack, mappingAction);
            });
          if (!retObj.ContainsKey(rpcName))
            retObj[rpcName] = value;
        }
      }
      finally
      {
        parseStack.Pop();
      }
      return retObj;
    }

    private object ParseStruct(IEnumerator<Node> iter, Type valueType, ParseStack parseStack, 
      MappingAction mappingAction, out Type parsedType)
    {
      parsedType = null;

      if (valueType.IsPrimitive)
      {
        throw new XmlRpcTypeMismatchException(parseStack.ParseType
          + " contains struct value where "
          + XmlRpcServiceInfo.GetXmlRpcTypeString(valueType)
          + " expected " + StackDump(parseStack));
      }
      if (valueType.IsGenericType
        && valueType.GetGenericTypeDefinition() == typeof(Nullable<>))
      {
        valueType = valueType.GetGenericArguments()[0];
      }
      object retObj;
      try
      {
        retObj = Activator.CreateInstance(valueType);
      }
      catch (Exception)
      {
        throw new XmlRpcTypeMismatchException(parseStack.ParseType
          + " contains struct value where "
          + XmlRpcServiceInfo.GetXmlRpcTypeString(valueType)
          + " expected (as type " + valueType.Name + ") "
          + StackDump(parseStack));
      }
      // Note: mapping action on a struct is only applied locally - it 
      // does not override the global mapping action when members of the 
      // struct are parsed
      MappingAction localAction = mappingAction;
      if (valueType != null)
      {
        parseStack.Push("struct mapped to type " + valueType.Name);
        localAction = StructMappingAction(valueType, mappingAction);
      }
      else
      {
        parseStack.Push("struct");
      }
      // create map of field names and remove each name from it as 
      // processed so we can determine which fields are missing
      var names = new List<string>();
      CreateFieldNamesMap(valueType, names);
      int fieldCount = 0;
      List<string> rpcNames = new List<string>();
      try
      {
        while (iter.MoveNext() && iter.Current is StructMember)
        {
          string rpcName = (iter.Current as StructMember).Value;
          if (rpcNames.Contains(rpcName))
            throw new XmlRpcInvalidXmlRpcException(parseStack.ParseType
              + " contains struct value with duplicate member "
              + rpcName
              + " " + StackDump(parseStack));
          rpcNames.Add(rpcName);

          string name = GetStructName(valueType, rpcName) ?? rpcName;
          MemberInfo mi = valueType.GetField(name);
          if (mi == null) mi = valueType.GetProperty(name);
          if (mi == null)
            continue;
          if (names.Contains(name))
              names.Remove(name);
          else
          {
              if (Attribute.IsDefined(mi, typeof(NonSerializedAttribute)))
              {
                  parseStack.Push(String.Format("member {0}", name));
                  throw new XmlRpcNonSerializedMember("Cannot map XML-RPC struct "
                  + "member onto member marked as [NonSerialized]: "
                  + " " + StackDump(parseStack));
              }
          }
          Type memberType = mi.MemberType == MemberTypes.Field
          ? (mi as FieldInfo).FieldType : (mi as PropertyInfo).PropertyType;
          string parseMsg = valueType == null
              ? String.Format("member {0}", name)
              : String.Format("member {0} mapped to type {1}", name, memberType.Name);

          iter.MoveNext();
          object valObj = OnStack(parseMsg, parseStack, delegate()
          {
              return ParseValueNode(iter, memberType, parseStack, mappingAction);
          });

          if (mi.MemberType == MemberTypes.Field)
            (mi as FieldInfo).SetValue(retObj, valObj);
          else
            (mi as PropertyInfo).SetValue(retObj, valObj, null);
          fieldCount++;
        }

        if (localAction == MappingAction.Error && names.Count > 0)
          ReportMissingMembers(valueType, names, parseStack);
        return retObj;
      }
      finally
      {
        parseStack.Pop();
      }
    }

    private object ParseArray(IEnumerator<Node> iter, Type valType, 
      ParseStack parseStack, MappingAction mappingAction, 
      out Type parsedType)
    {
      parsedType = null;
      // required type must be an array
      if (valType != null
        && !(valType.IsArray == true
            || valType == typeof(Array)
            || valType == typeof(object)))
      {
        throw new XmlRpcTypeMismatchException(parseStack.ParseType
          + " contains array value where "
          + XmlRpcServiceInfo.GetXmlRpcTypeString(valType)
          + " expected " + StackDump(parseStack));
      }
      if (valType != null)
      {
        XmlRpcType xmlRpcType = XmlRpcServiceInfo.GetXmlRpcType(valType);
        if (xmlRpcType == XmlRpcType.tMultiDimArray)
        {
          parseStack.Push("array mapped to type " + valType.Name);
          Object ret = ParseMultiDimArray(iter, valType, parseStack,
            mappingAction);
          return ret;
        }
        parseStack.Push("array mapped to type " + valType.Name);
      }
      else
        parseStack.Push("array");

      var values = new List<object>();
      Type elemType = DetermineArrayItemType(valType);

      bool bGotType = false;
      Type useType = null;

      while (iter.MoveNext() && iter.Current is ValueNode)
      {
        parseStack.Push(String.Format("element {0}", values.Count));
        object value = ParseValueNode(iter, elemType, parseStack, mappingAction);
        values.Add(value);
        parseStack.Pop();
      }

      foreach (object value in values)
      {
        if (bGotType == false)
        {
          useType = value.GetType();
          bGotType = true;
        }
        else
        {
          if (useType != value.GetType())
            useType = null;
        }
      }

      Object[] args = new Object[1];
      args[0] = values.Count;
      Object retObj = null;
      if (valType != null
        && valType != typeof(Array)
        && valType != typeof(object))
      {
        retObj = CreateArrayInstance(valType, args);
      }
      else
      {
        if (useType == null)
          retObj = CreateArrayInstance(typeof(object[]), args);
        else
          retObj = Array.CreateInstance(useType, (int)args[0]); ;
      }
      for (int j = 0; j < values.Count; j++)
      {
        ((Array)retObj).SetValue(values[j], j);
      }

      parseStack.Pop();

      return retObj;
    }

    private static Type DetermineArrayItemType(Type valType)
    {
      Type elemType = null;
      if (valType != null
        && valType != typeof(Array)
        && valType != typeof(object))
      {
#if (!COMPACT_FRAMEWORK)
        elemType = valType.GetElementType();
#else
            string[] checkMultiDim = Regex.Split(ValueType.FullName, 
              "\\[\\]$");
            // determine assembly of array element type
            Assembly asmbly = ValueType.Assembly;
            string[] asmblyName = asmbly.FullName.Split(',');
            string elemTypeName = checkMultiDim[0] + ", " + asmblyName[0]; 
            elemType = Type.GetType(elemTypeName);
#endif
      }
      else
      {
        elemType = typeof(object);
      }
      return elemType;
    }


    private void CheckImplictString(Type valType, ParseStack parseStack)
    {
      if (valType != null && valType != typeof(string))
      {
        throw new XmlRpcTypeMismatchException(parseStack.ParseType
          + " contains implicit string value where "
          + XmlRpcServiceInfo.GetXmlRpcTypeString(valType)
          + " expected " + StackDump(parseStack));
      }
    }

    private object ParseMultiDimArray(IEnumerator<Node> iter, Type valType, ParseStack parseStack, MappingAction mappingAction)
    {
      throw new NotImplementedException();
    }


    public Object ParseValueElement(
      XmlReader rdr,
      Type valType,
      ParseStack parseStack,
      MappingAction mappingAction)
    {
      var iter = XmlRpcParser.ParseValue(rdr).GetEnumerator();
      iter.MoveNext();

      return ParseValueNode(iter, valType, parseStack, mappingAction);
    }


//    private object ParseArray(XmlReader rdr, Type valType, ParseStack parseStack,
//      MappingAction mappingAction, out Type ParsedType, out Type ParsedArrayType)
//    {
//      ParsedType = null;
//      ParsedArrayType = null;
//      // required type must be an array
//      if (valType != null
//        && !(valType.IsArray == true
//            || valType == typeof(Array)
//            || valType == typeof(object)))
//      {
//        throw new XmlRpcTypeMismatchException(parseStack.ParseType
//          + " contains array value where "
//          + XmlRpcServiceInfo.GetXmlRpcTypeString(valType)
//          + " expected " + StackDump(parseStack));
//      }
//      if (valType != null)
//      {
//        XmlRpcType xmlRpcType = XmlRpcServiceInfo.GetXmlRpcType(valType);
//        if (xmlRpcType == XmlRpcType.tMultiDimArray)
//        {
//          parseStack.Push("array mapped to type " + valType.Name);
//          Object ret = ParseMultiDimArray(rdr, valType, parseStack,
//            mappingAction);
//          return ret;
//        }
//        parseStack.Push("array mapped to type " + valType.Name);
//      }
//      else
//        parseStack.Push("array");

//      MoveToChild(rdr, "data");

//      //XmlNode[] childNodes = SelectNodes(dataNode, "value");
//      //int nodeCount = childNodes.Length;
//      //Object[] elements = new Object[nodeCount];

//      var values = new List<object>();
//      Type elemType = DetermineArrayItemType(valType);



//      bool bGotType = false;
//      Type useType = null;

//      bool gotValue = MoveToChild(rdr, "value");
//      while (gotValue)
//      {
//        parseStack.Push(String.Format("element {0}", values.Count));
//        object value = ParseValueElement(rdr, elemType, parseStack, mappingAction);
//        values.Add(value);
//        MoveToEndElement(rdr, "value", 0);
//        gotValue = rdr.ReadToNextSibling("value");
//        parseStack.Pop();
//      }


//      foreach (object value in values)
//      {
//        //if (bGotType == false)
//        //{
//        //  useType = parsedArrayType;
//        //  bGotType = true;
//        //}
//        //else
//        //{
//        //  if (useType != parsedArrayType)
//        //    useType = null;
//        //}
//      }

//      Object[] args = new Object[1];
//      args[0] = values.Count;
//      Object retObj = null;
//      if (valType != null
//        && valType != typeof(Array)
//        && valType != typeof(object))
//      {
//        retObj = CreateArrayInstance(valType, args);
//      }
//      else
//      {
//        if (useType == null)
//          retObj = CreateArrayInstance(typeof(object[]), args);
//        else
//          retObj = CreateArrayInstance(useType, args);
//      }
//      for (int j = 0; j < values.Count; j++)
//      {
//        ((Array)retObj).SetValue(values[j], j);
//      }

//      parseStack.Pop();

//      return retObj;
//    }

//    private static Type DetermineArrayItemType(Type valType)
//    {
//      Type elemType = null;
//      if (valType != null
//        && valType != typeof(Array)
//        && valType != typeof(object))
//      {
//#if (!COMPACT_FRAMEWORK)
//        elemType = valType.GetElementType();
//#else
//        string[] checkMultiDim = Regex.Split(ValueType.FullName, 
//          "\\[\\]$");
//        // determine assembly of array element type
//        Assembly asmbly = ValueType.Assembly;
//        string[] asmblyName = asmbly.FullName.Split(',');
//        string elemTypeName = checkMultiDim[0] + ", " + asmblyName[0]; 
//        elemType = Type.GetType(elemTypeName);
//#endif
//      }
//      else
//      {
//        elemType = typeof(object);
//      }
//      return elemType;
//    }

//    private object ParseMultiDimArray(XmlReader rdr, Type valType, ParseStack parseStack, MappingAction mappingAction)
//    {
//      throw new NotImplementedException();
//    }

    private static void CreateFieldNamesMap(Type valueType, List<string> names)
    {
        foreach (FieldInfo fi in valueType.GetFields())
        {
            if (Attribute.IsDefined(fi, typeof(NonSerializedAttribute)))
                continue;
            names.Add(fi.Name);
        }
        foreach (PropertyInfo pi in valueType.GetProperties())
        {
            if (Attribute.IsDefined(pi, typeof(NonSerializedAttribute)))
                continue;
            names.Add(pi.Name);
        }
    }


    private void CheckExpectedType(Type actualType, Type expectedType, ParseStack parseStack)
    {
      if (actualType != null && actualType != typeof(Object)
        && actualType != expectedType 
        && (expectedType.IsValueType 
          && actualType != typeof(Nullable<>).MakeGenericType(expectedType)))
      {
        throw new XmlRpcTypeMismatchException(parseStack.ParseType +
          " contains "
          + XmlRpcServiceInfo.GetXmlRpcTypeString(expectedType)
          + "value where "
          + XmlRpcServiceInfo.GetXmlRpcTypeString(actualType)
          + " expected " + StackDump(parseStack));
      }
    }

    delegate T Func<T>();

    private T OnStack<T>(string p, ParseStack parseStack, Func<T> func)
    {
      parseStack.Push(p);
      try 
      {
        return func();
      }
      finally
      {
        parseStack.Pop();
      }
    }

    void ReportMissingMembers(
      Type valueType,
      List<string> names,
      ParseStack parseStack)
    {
      StringBuilder sb = new StringBuilder();
      int errorCount = 0;
      string sep = "";
      foreach (string s in names)
      {
        MappingAction memberAction = MemberMappingAction(valueType, s,
          MappingAction.Error);
        if (memberAction == MappingAction.Error)
        {
          sb.Append(sep);
          sb.Append(s);
          sep = " ";
          errorCount++;
        }
      }
      if (errorCount > 0)
      {
        string plural = "";
        if (errorCount > 1)
          plural = "s";
        throw new XmlRpcTypeMismatchException(parseStack.ParseType
          + " contains struct value with missing non-optional member"
          + plural + ": " + sb.ToString() + " " + StackDump(parseStack));
      }
    }

    string GetStructName(Type ValueType, string XmlRpcName)
    {
      // given a member name in an XML-RPC struct, check to see whether
      // a field has been associated with this XML-RPC member name, return
      // the field name if it has else return null
      if (ValueType == null)
        return null;
      foreach (FieldInfo fi in ValueType.GetFields())
      {
        Attribute attr = Attribute.GetCustomAttribute(fi,
          typeof(XmlRpcMemberAttribute));
        if (attr != null
          && attr is XmlRpcMemberAttribute
          && ((XmlRpcMemberAttribute)attr).Member == XmlRpcName)
        {
          string ret = fi.Name;
          return ret;
        }
      }
      foreach (PropertyInfo pi in ValueType.GetProperties())
      {
        Attribute attr = Attribute.GetCustomAttribute(pi,
          typeof(XmlRpcMemberAttribute));
        if (attr != null
          && attr is XmlRpcMemberAttribute
          && ((XmlRpcMemberAttribute)attr).Member == XmlRpcName)
        {
          string ret = pi.Name;
          return ret;
        }
      }
      return null;
    }

    MappingAction StructMappingAction(
      Type type,
      MappingAction currentAction)
    {
      // if struct member has mapping action attribute, override the current
      // mapping action else just return the current action
      if (type == null)
        return currentAction;
      Attribute attr = Attribute.GetCustomAttribute(type,
        typeof(XmlRpcMissingMappingAttribute));
      if (attr != null)
        return ((XmlRpcMissingMappingAttribute)attr).Action;
      return currentAction;
    }

    MappingAction MemberMappingAction(
      Type type,
      string memberName,
      MappingAction currentAction)
    {
      // if struct member has mapping action attribute, override the current
      // mapping action else just return the current action
      if (type == null)
        return currentAction;
      Attribute attr = null;
      FieldInfo fi = type.GetField(memberName);
      if (fi != null)
        attr = Attribute.GetCustomAttribute(fi,
          typeof(XmlRpcMissingMappingAttribute));
      else
      {
        PropertyInfo pi = type.GetProperty(memberName);
        attr = Attribute.GetCustomAttribute(pi,
          typeof(XmlRpcMissingMappingAttribute));
      }
      if (attr != null)
        return ((XmlRpcMissingMappingAttribute)attr).Action;
      return currentAction;
    }

    XmlRpcFaultException ParseFault(
    IEnumerator<Node> iter,
    ParseStack parseStack,
    MappingAction mappingAction)
    {
      iter.MoveNext();  // move to StructValue
      Type parsedType;
      var faultStruct = ParseHashtable(iter, null, parseStack, mappingAction,
        out parsedType) as XmlRpcStruct;
      object faultCode = faultStruct["faultCode"];
      object faultString = faultStruct["faultString"];
      if (faultCode is string)
      {
        int value;
        if (!Int32.TryParse(faultCode as string, out value))
          throw new XmlRpcInvalidXmlRpcException("faultCode not int or string");
        faultCode = value;
      }
      return new XmlRpcFaultException((int)faultCode, (string)faultString);
    }

    struct FaultStruct
    {
      public int faultCode;
      public string faultString;
    }

    struct FaultStructStringCode
    {
      public string faultCode;
      public string faultString;
    }

    string StackDump(ParseStack parseStack)
    {
      StringBuilder sb = new StringBuilder();
      foreach (string elem in parseStack)
      {
        sb.Insert(0, elem);
        sb.Insert(0, " : ");
      }
      sb.Insert(0, parseStack.ParseType);
      sb.Insert(0, "[");
      sb.Append("]");
      return sb.ToString();
    }

    // TODO: following to return Array?
    object CreateArrayInstance(Type type, object[] args)
    {
#if (!COMPACT_FRAMEWORK)
      return Activator.CreateInstance(type, args);
#else
    Object Arr = Array.CreateInstance(type.GetElementType(), (int)args[0]);
    return Arr;
#endif
    }

    bool IsStructParamsMethod(MethodInfo mi)
    {
      if (mi == null)
        return false;
      bool ret = false;
      Attribute attr = Attribute.GetCustomAttribute(mi,
        typeof(XmlRpcMethodAttribute));
      if (attr != null)
      {
        XmlRpcMethodAttribute mattr = (XmlRpcMethodAttribute)attr;
        ret = mattr.StructParams;
      }
      return ret;
    }
  }
}


