﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Xml;

namespace CookComputing.XmlRpc
{
  public static class XmlRpcParser
  {
    public static IEnumerable<Node> ParseRequest(XmlReader rdr)
    {
      rdr.MoveToContent();
      //Assert(rdr, "methodCall");
      int mcDepth = rdr.Depth;

      MoveToChild(rdr, "methodName", true);
      int mnDepth = rdr.Depth;
      string methodName = rdr.ReadElementContentAsString();
      if (methodName == "")
        throw new XmlRpcInvalidXmlRpcException(
          "Request XML not valid XML-RPC - empty methodName.");
      yield return new MethodName(methodName);
      if (MovetoSibling(rdr, "params", false))
      {
        yield return new ParamsNode();
        int psDepth = rdr.Depth;
        bool gotP = MoveToChild(rdr, "param");
        while (gotP)
        {
          foreach (Node node in ParseParam(rdr))
            yield return node;
          gotP = MovetoSibling(rdr, "param");
        }
        MoveToEndElement(rdr, psDepth);
      }
      MoveToEndElement(rdr, mcDepth);
    }

    public static IEnumerable<Node> ParseResponse(XmlReader rdr)
    {
      // TODO: handle fault response
      yield return new ResponseNode();
      rdr.MoveToContent();
      //Assert(rdr, "methodCall");
      // TODO: check is methodCall
      int mrDepth = rdr.Depth;
      MoveToChild(rdr, "params", true);
      int psDepth = rdr.Depth;
      bool gotP = MoveToChild(rdr, "param");
      if (gotP)
      {
        foreach (Node node in ParseParam(rdr))
          yield return node;
      }
      MoveToEndElement(rdr, psDepth);
      MoveToEndElement(rdr, mrDepth);
    }

    private static IEnumerable<Node> ParseParam(XmlReader rdr)
    {
      int pDepth = rdr.Depth;
      //yield return new ParamNode();
      MoveToChild(rdr, "value", true);
      foreach (Node node in ParseValue(rdr))
        yield return node;
      MoveToEndElement(rdr, pDepth);
    }

    public static IEnumerable<Node> ParseValue(XmlReader rdr)
    {
      int vDepth = rdr.Depth;
      if (rdr.IsEmptyElement)
      {
        yield return new StringValue("", true);
      }
      else
      {
        rdr.Read(); // TODO: check all return values from rdr.Read()
        if (rdr.NodeType == XmlNodeType.Text)
        {
          yield return new StringValue(rdr.Value,true);
        }
        else
        {
          string strValue = "";
          if (rdr.NodeType == XmlNodeType.Whitespace)
          {
            strValue = rdr.Value;
            rdr.Read();
          }
          if (rdr.NodeType == XmlNodeType.EndElement)
          {
            yield return new StringValue(strValue, true);
          }
          else if (rdr.NodeType == XmlNodeType.Element)
          {
            switch (rdr.Name)
            {
              case "string":
                yield return new StringValue(rdr.ReadElementContentAsString(), false);
                break;
              case "int":
              case "i4":
                yield return new IntValue(rdr.ReadElementContentAsString());
                break;
              case "i8":
                yield return new LongValue(rdr.ReadElementContentAsString());
                break;
              case "double":
                yield return new DoubleValue(rdr.ReadElementContentAsString());
                break;
              case "dateTime.iso8601":
                yield return new DateTimeValue(rdr.ReadElementContentAsString());
                break;
              case "boolean":
                yield return new BooleanValue(rdr.ReadElementContentAsString());
                break;
              case "base64":
                yield return new Base64Value(rdr.ReadElementContentAsString());
                break;
              case "struct":
                foreach (var node in ParseStruct(rdr))
                  yield return node;
                break;
              case "array":
                foreach (var node in ParseArray(rdr))
                  yield return node;
                break;
              case "nil":
                yield return new NilValue();
                break;
            }
          }
        }
      }
      MoveToEndElement(rdr, vDepth);
    }

    private static IEnumerable<Node> ParseArray(XmlReader rdr)
    {
      yield return new ArrayValue();
      int aDepth = rdr.Depth;
      MoveToChild(rdr, "data");
      bool gotV = MoveToChild(rdr, "value");
      int vDepth = rdr.Depth;
      while (gotV)
      {
        foreach (Node node in ParseValue(rdr))
          yield return node;
        gotV = MovetoSibling(rdr, "value");
      }
    }

    private static IEnumerable<Node> ParseStruct(XmlReader rdr)
    {
      yield return new StructValue();
      int sDepth = rdr.Depth;
      bool gotM = MoveToChild(rdr, "member");
      int mDepth = rdr.Depth;
      while (gotM)
      {
        MoveToChild(rdr, "name", true);
        string name = rdr.ReadElementContentAsString();
        yield return new StructMember(name);
        MoveOverWhiteSpace(rdr);
        if (!(rdr.NodeType == XmlNodeType.Element && rdr.Name == "value"))
          throw new Exception();
        foreach (Node node in ParseValue(rdr))
          yield return node;
        MoveToEndElement(rdr, mDepth);
        gotM = MovetoSibling(rdr, "member");
      }
      MoveToEndElement(rdr, sDepth);
    }

    private static bool MovetoSibling(XmlReader rdr, string p)
    {
      return MovetoSibling(rdr, p, false);
    }

    private static bool MovetoSibling(XmlReader rdr, string p, bool required)
    {
      if (!rdr.IsEmptyElement && rdr.NodeType == XmlNodeType.Element && rdr.Name == p)
        return true;
      int depth = rdr.Depth;
      rdr.Read();
      while (rdr.Depth >= depth)
      {
        if (rdr.Depth == (depth) && rdr.NodeType == XmlNodeType.Element
            && rdr.Name == p)
          return true;
        if (!rdr.Read())
          break;
      }
      if (required)
        throw new XmlRpcInvalidXmlRpcException(string.Format("Missing element {0}", p));
      return false;
    }

    private static bool MoveToEndElement(XmlReader rdr, int mcDepth)
    {
      if (rdr.Depth == mcDepth && rdr.IsEmptyElement)
        return true;
      if (rdr.Depth == mcDepth && rdr.NodeType == XmlNodeType.EndElement)
        return true;
      while (rdr.Depth >= mcDepth)
      {
        rdr.Read();
        if (rdr.Depth == mcDepth && rdr.NodeType == XmlNodeType.EndElement)
          return true;
      }
      return false;
    }

    private static bool MoveToChild(XmlReader rdr, string nodeName)
    {
      return MoveToChild(rdr, nodeName, false);
    }

    private static bool MoveToChild(XmlReader rdr, string nodeName, bool required)
    {
      int depth = rdr.Depth;
      if (rdr.IsEmptyElement)
      {
        if (required)
          throw new XmlRpcInvalidXmlRpcException(string.Format("Missing element {0}", nodeName));
        return false;
      }
      rdr.Read();
      while (rdr.Depth > depth)
      {
        if (rdr.Depth == (depth + 1) && rdr.NodeType == XmlNodeType.Element
            && rdr.Name == nodeName)
          return true;
        rdr.Read();
      }
      if (required)
        throw new XmlRpcInvalidXmlRpcException(string.Format("Missing element {0}", nodeName));
      return false;
    }

    private static void MoveOverWhiteSpace(XmlReader rdr)
    {
      while (rdr.NodeType == XmlNodeType.Whitespace
        || rdr.NodeType == XmlNodeType.SignificantWhitespace)
        rdr.Read();
    }
  }


  public class Node
  {

  }

  public class ValueNode : Node
  {
    public ValueNode()
    {
    }

    public ValueNode(string value)
    {
      Value = value;
    }

    public ValueNode(string value, bool implicitValue)
    {
      Value = value;
      ImplicitValue = implicitValue;
    }

    public string Value { get; set; }
    public bool ImplicitValue { get; private set; }
  }

  public class StringValue : ValueNode
  {
    public StringValue(string value, bool implicitValue)
      : base(value, implicitValue)
    {
    }
  }

  public class IntValue : ValueNode
  {
    public IntValue(string value)
      : base(value)
    {
    }
  }

  public class LongValue : ValueNode
  {
    public LongValue(string value)
      : base(value)
    {
    }
  }

  public class DoubleValue : ValueNode
  {
    public DoubleValue(string value)
      : base(value)
    {
    }
  }

  public class BooleanValue : ValueNode
  {
    public BooleanValue(string value)
      : base(value)
    {
    }
  }

  public class DateTimeValue : ValueNode
  {
    public DateTimeValue(string value)
      : base(value)
    {
    }
  }

  public class Base64Value : ValueNode
  {
    public Base64Value(string value)
      : base(value)
    {
    }
  }

  public class MethodName : Node
  {
    public MethodName(string value)
    {
      Name = value;
    }

    public string Name { get; set; }
  }

  public class StructMember : ValueNode
  {
    public StructMember(string value)
      : base(value)
    {
    }
  }


  public class FaultNode : Node
  {
  }

  public class ResponseNode : Node
  {
  }

  public class ParamsNode : Node
  {
  }

  public class ParamNode : Node
  {
  }

  public class StructValue : Node
  {

  }

  public class ArrayValue : Node
  {

  }

  public class NilValue : ValueNode
  {

  }
}
