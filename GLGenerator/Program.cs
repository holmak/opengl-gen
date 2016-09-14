using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;

namespace GLGenerator
{
    class Program
    {
        public static string[] IncludedFeatures = new string[]
        {
            "GL_VERSION_1_0",
            "GL_VERSION_1_1",
            "GL_VERSION_1_2",
            "GL_VERSION_1_3",
            "GL_VERSION_1_4",
            "GL_VERSION_1_5",
            "GL_VERSION_2_0",
            "GL_VERSION_2_1",
            "GL_VERSION_3_0",
            "GL_VERSION_3_1",
            "GL_VERSION_3_2",
            "GL_VERSION_3_3",
        };
        
        static void Main(string[] args)
        {
            XmlDocument document = new XmlDocument();
            document.Load("Registry/gl.xml");

            // Enum and command definitions.
            Dictionary<string, List<string>> groups = new Dictionary<string, List<string>>();
            Dictionary<string, uint> enumValues = new Dictionary<string, uint>();
            Dictionary<string, GLCommand> commands = new Dictionary<string, GLCommand>();

            // These are the names of commands and enum values supported by the selected GL features and extensions.
            List<string> includedEnums = new List<string>();
            List<string> includedCommands = new List<string>();

            //==============================================================================
            // Parse groups (subsets of GLenum values; these translate to C# enums)
            //==============================================================================

            foreach (XmlNode group in document.SelectNodes("/registry/groups/group"))
            {
                List<string> members = group.SelectNodes("enum").Cast<XmlNode>()
                    .Select(x => x.Attributes["name"].Value).ToList();

                groups.Add(group.Attributes["name"].Value, members);
            }

            //==============================================================================
            // Parse enum numeric values
            //==============================================================================

            foreach (XmlNode constant in document.SelectNodes("/registry/enums/enum"))
            {
                string name = constant.Attributes["name"].Value;
                string literal = constant.Attributes["value"].Value;
                string api = GetAttributeOrNull(constant, "api");
                string type = GetAttributeOrNull(constant, "type");

                // Ignore enum values that are part of other APIs, like GLES.
                if (api != null && api != "gl")
                {
                    continue;
                }

                if (type != null)
                {
                    // Ignore enums with non-default types. (There are a couple of enums with 64-bit values.)
                    continue;
                }

                uint value;
                if (literal.StartsWith("0x"))
                {
                    value = Convert.ToUInt32(literal, 16);
                }
                else if (literal.StartsWith("-"))
                {
                    value = (uint)int.Parse(literal, NumberStyles.Integer);
                }
                else
                {
                    value = uint.Parse(literal, NumberStyles.Integer);
                }

                enumValues.Add(name, value);
            }

            //==============================================================================
            // Parse function names and types
            //==============================================================================

            foreach (XmlNode commandNode in document.SelectNodes("/registry/commands/command"))
            {
                GLCommand command = new GLCommand();

                XmlNode prototypeNode = commandNode.SelectSingleNode("proto");
                ParseTypeAndName(prototypeNode, out command.ReturnType, out command.Name);

                foreach (XmlNode paramNode in commandNode.SelectNodes("param"))
                {
                    string type, name;
                    ParseTypeAndName(paramNode, out type, out name);
                    command.ParamTypes.Add(type);
                    command.ParamNames.Add(name);
                }

                commands.Add(command.Name, command);
            }

            //==============================================================================
            // Parse feature sets
            //==============================================================================

            foreach (XmlNode feature in document.SelectNodes("/registry/feature"))
            {
                string featureName = GetAttributeOrNull(feature, "name");
                if (IncludedFeatures.Contains(featureName))
                {
                    var removedEnums = feature.SelectNodes("remove/enum")
                        .Cast<XmlNode>()
                        .Select(x => GetAttributeOrNull(x, "name"));

                    var removedCommands = feature.SelectNodes("remove/command")
                        .Cast<XmlNode>()
                        .Select(x => GetAttributeOrNull(x, "name"));

                    var requiredEnums = feature.SelectNodes("require/enum")
                        .Cast<XmlNode>()
                        .Select(x => GetAttributeOrNull(x, "name"));

                    var requiredCommands = feature.SelectNodes("require/command")
                        .Cast<XmlNode>()
                        .Select(x => GetAttributeOrNull(x, "name"));

                    foreach (string name in removedEnums)
                    {
                        includedEnums.Remove(name);
                    }

                    foreach (string name in removedCommands)
                    {
                        includedCommands.Remove(name);
                    }

                    includedEnums.AddRange(requiredEnums);
                    includedCommands.AddRange(requiredCommands);
                }
            }

            using (StreamWriter output = new StreamWriter("Registry/GL.cs"))
            {
                output.WriteLine("public static class GL");
                output.WriteLine("{");

                //==============================================================================
                // Generate enum declarations
                //==============================================================================

                foreach (var pair in groups)
                {
                    string name = pair.Key;
                    List<string> members = pair.Value;

                    output.WriteLine("    public enum {0} : uint", name);
                    output.WriteLine("    {");
                    foreach (string member in members)
                    {
                        if (includedEnums.Contains(member))
                        {
                            uint value;
                            if (enumValues.TryGetValue(member, out value))
                            {
                                output.WriteLine("        {0} = 0x{1:X8},", member, value);
                            }
                        }
                    }
                    output.WriteLine("    }");
                    output.WriteLine();
                }

                //==============================================================================
                // Generate function pointers for GL functions
                //==============================================================================

                foreach (string commandName in includedCommands)
                {
                    GLCommand command = commands[commandName];
                    string delegateName = GetDelegateName(command.Name);
                    output.Write("    public delegate {0} {1}(", command.ReturnType, delegateName);
                    for (int i = 0; i < command.ParamNames.Count; i++)
                    {
                        string comma = (i < command.ParamNames.Count - 1) ? ", " : "";
                        output.Write("{0} {1}{2}", command.ParamTypes[i], command.ParamNames[i], comma);
                    }
                    output.WriteLine(");");
                    output.WriteLine("    public static {0} {1};", delegateName, command.Name);
                    output.WriteLine();
                }

                //==============================================================================
                // Generate code to load the GL function pointers
                //==============================================================================

                output.WriteLine("    public static void LoadAll()");
                output.WriteLine("    {");

                foreach (string commandName in includedCommands)
                {
                    GLCommand command = commands[commandName];
                    string delegateName = GetDelegateName(command.Name);
                    output.WriteLine("        {0} = Marshal.GetDelegateForFunctionPointer<{1}>(SDL.SDL_GL_GetProcAddress(\"{0}\"));",
                        command.Name, delegateName);
                }

                output.WriteLine("    }");

                output.WriteLine("}");
            }
        }

        static void ParseTypeAndName(XmlNode node, out string type, out string name)
        {
            string text = node.InnerText;
            name = node.SelectSingleNode("name").InnerText;
            type = text.Substring(0, text.Length - name.Length).Trim();
        }

        static string GetAttributeOrNull(XmlNode node, string attribute)
        {
            XmlNode attrNode = node.Attributes[attribute];
            if (attrNode != null)
            {
                return attrNode.Value;
            }
            else
            {
                return null;
            }
        }

        static string GetDelegateName(string commandName)
        {
            return commandName + "Delegate";
        }
    }

    class GLCommand
    {
        public string Name;
        public List<string> ParamTypes = new List<string>();
        public List<string> ParamNames = new List<string>();
        public string ReturnType;
    }
}
