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
                string returnTypeGroup = GetAttributeOrNull(prototypeNode, "group");

                foreach (XmlNode paramNode in commandNode.SelectNodes("param"))
                {
                    string name;
                    GLType type;
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

                    foreach (string name in requiredEnums)
                    {
                        if (!includedEnums.Contains(name))
                        {
                            includedEnums.Add(name);
                        }
                    }

                    foreach (string name in requiredCommands)
                    {
                        if (!includedCommands.Contains(name))
                        {
                            includedCommands.Add(name);
                        }
                    }
                }
            }

            using (StreamWriter output = new StreamWriter("Registry/GL.cs"))
            {
                output.WriteLine("// This file was generated by the OpenGL-CS code generating tool.");
                output.WriteLine("// https://bitbucket.org/holmak/opengl-cs");
                output.WriteLine("//");
                output.WriteLine("// GL versions included:");
                foreach (string version in IncludedFeatures)
                {
                    output.WriteLine("//   " + version);
                }
                output.WriteLine();
                output.WriteLine("using System;");
                output.WriteLine("using System.Runtime.InteropServices;");
                output.WriteLine("using System.Text;");
                output.WriteLine("using SDL2;");
                output.WriteLine();
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
                // Generate an enum containing all GL constants
                //==============================================================================

                output.WriteLine("    public enum GLenum : uint");
                output.WriteLine("    {");
                foreach (string member in includedEnums)
                {
                    uint value;
                    if (enumValues.TryGetValue(member, out value))
                    {
                        output.WriteLine("        {0} = 0x{1:X8},", member, value);
                    }
                }
                output.WriteLine("    }");
                output.WriteLine();

                //==============================================================================
                // Generate function pointers for GL functions
                //==============================================================================

                foreach (string commandName in includedCommands)
                {
                    GLCommand command = commands[commandName];
                    output.WriteLine("    public static DelegateTypes.{0} {0};", command.Name);
                }
                output.WriteLine();

                //==============================================================================
                // Generate code to load the GL function pointers
                //==============================================================================

                output.WriteLine("    private static T Load<T>(string name)");
                output.WriteLine("    {");
                output.WriteLine("        return Marshal.GetDelegateForFunctionPointer<T>(SDL.SDL_GL_GetProcAddress(name));");
                output.WriteLine("    }");

                output.WriteLine("    public static void LoadAll()");
                output.WriteLine("    {");

                foreach (string commandName in includedCommands)
                {
                    GLCommand command = commands[commandName];
                    output.WriteLine("        {0} = Load<DelegateTypes.{0}>(\"{0}\");", command.Name);
                }

                output.WriteLine("    }");
                output.WriteLine();

                //==============================================================================
                // Generate delegate types
                //==============================================================================

                output.WriteLine("    public static class DelegateTypes");
                output.WriteLine("    {");

                foreach (string commandName in includedCommands)
                {
                    GLCommand command = commands[commandName];
                    output.Write("        public delegate {0} {1}(", TranslateType(groups.Keys, true, command.ReturnType), command.Name);
                    for (int i = 0; i < command.ParamNames.Count; i++)
                    {
                        string comma = (i < command.ParamNames.Count - 1) ? ", " : "";
                        output.Write("{0} {1}{2}", TranslateType(groups.Keys, false, command.ParamTypes[i]), TranslateName(command.ParamNames[i]), comma);
                    }
                    output.WriteLine(");");
                }

                output.WriteLine("    }");

                output.WriteLine("}");
            }
        }

        static void ParseTypeAndName(XmlNode node, out GLType type, out string name)
        {
            string text = node.InnerText;
            name = node.SelectSingleNode("name").InnerText;
            type = new GLType
            {
                Name = text.Substring(0, text.Length - name.Length).Trim(),
                Group = GetAttributeOrNull(node, "group"),
            };
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

        static string TranslateType(IEnumerable<string> definedGroups, bool isReturnType, GLType type)
        {
            Dictionary<string, string> translations = new Dictionary<string, string>
            {
                { "const GLbyte *", "/*const*/ byte[]" },
                { "const GLchar *", "/*const*/ string" },
                { "const GLchar *const*", "/*const*/ string[]" },
                { "const GLdouble *", "/*const*/ double[]" },
                { "const GLenum *", "/*const*/ IntPtr" },
                { "const GLfloat *", "/*const*/ float[]" },
                { "const GLint *", "/*const*/ int[]" },
                { "const GLshort *", "/*const*/ short[]" },
                { "const GLsizei *", "/*const*/ int[]" },
                { "const GLubyte *", "/*const*/ byte[]" },
                { "const GLuint *", "/*const*/ uint[]" },
                { "const GLushort *", "/*const*/ ushort[]" },
                { "const void *", "byte[]" },
                { "const void *const*", "/*const*/ IntPtr" },
                { "GLbitfield", "GLenum" },
                { "GLboolean *", "out bool" },
                { "GLboolean", "bool" },
                { "GLbyte *", "out byte" },
                { "GLchar *", "StringBuilder" },
                { "GLdouble *", "out double" },
                { "GLdouble", "double" },
                { "GLenum *", "IntPtr" },
                { "GLenum", "GLenum" },
                { "GLfloat *", "out float" },
                { "GLfloat", "float" },
                { "GLint *", "out int" },
                { "GLint", "int" },
                { "GLint64 *", "out long" },
                { "GLint64", "long" },
                { "GLintptr", "IntPtr" },
                { "GLshort", "short" },
                { "GLsizei *", "out int" },
                { "GLsizei", "int" },
                { "GLsizeiptr", "IntPtr" },
                { "GLsync", "IntPtr" },
                { "GLubyte", "byte" },
                { "GLuint *", "out uint" },
                { "GLuint", "uint" },
                { "GLuint64 *", "out ulong" },
                { "GLuint64", "ulong" },
                { "void *", "byte[]" },
                { "void **", "IntPtr" },
            };

            string translated;
            if (type.Name == "GLenum" && type.Group != null)
            {
                // Some groups are referenced, but never defined. In those cases, just use GLenum.
                if (!definedGroups.Contains(type.Group))
                {
                    return "GLenum";
                }

                return type.Group;
            }
            else if (type.Group == "String")
            {
                return "IntPtr";
            }
            else if (translations.TryGetValue(type.Name, out translated))
            {
                // Native functions can't return C# arrays.
                if (isReturnType && translated == "byte[]")
                {
                    return "IntPtr";
                }

                return translated;
            }
            else
            {
                return type.Name;
            }
        }

        static string TranslateName(string name)
        {
            // Escape identifiers that are also C# reserved tokens.
            string[] reservedTokens = new string[]
            {
                "params",
                "ref",
                "string",
            };

            if (reservedTokens.Contains(name))
            {
                name = "@" + name;
            }

            return name;
        }
    }

    class GLCommand
    {
        public string Name;
        public List<GLType> ParamTypes = new List<GLType>();
        public List<string> ParamNames = new List<string>();
        public GLType ReturnType;
    }

    struct GLType
    {
        public string Name;
        public string Group;
    }
}
