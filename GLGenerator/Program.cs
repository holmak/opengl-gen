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
            //"GL_VERSION_4_0",
            //"GL_VERSION_4_1",
            //"GL_VERSION_4_2",
            //"GL_VERSION_4_3",
            //"GL_VERSION_4_4",
            //"GL_VERSION_4_5",
        };

        public static string[] IncludedExtensions = new string[]
        {
            //"GL_EXT_texture_compression_s3tc",
            "GL_KHR_debug",
        };

        public const bool GenerateC = true;
        public const bool GenerateCSharp = true;
        public static readonly string OutputFolder = "Output";
        public static readonly string ExtraPrefix = "";

        /// <summary>
        /// If true, GL functions that should be present but are not are silently ignored.
        /// (The program will crash when the missing function is called, instead of at load-time.)
        /// </summary>
        public const bool AllowMissingFunctions = false;

        static void Main(string[] args)
        {
            Directory.CreateDirectory(OutputFolder);

            XmlDocument document = new XmlDocument();
            document.Load("Registry/gl.xml");

            // Enum and command definitions.
            List<string> typedefs = new List<string>();
            Dictionary<string, List<string>> groups = new Dictionary<string, List<string>>();
            Dictionary<string, uint> enumValues = new Dictionary<string, uint>();
            Dictionary<string, GLCommand> commands = new Dictionary<string, GLCommand>();

            // These are the names of commands and enum values supported by the selected GL features and extensions.
            List<string> includedEnums = new List<string>();
            List<string> includedCommands = new List<string>();

            //==============================================================================
            // Parse typedefs
            //==============================================================================

            foreach (XmlNode type in document.SelectNodes("/registry/types/type"))
            {
                if (!type.InnerText.StartsWith("#include"))
                {
                    typedefs.Add(type.InnerText);
                }
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
                string groupNames = GetAttributeOrNull(constant, "group");

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

                if (groupNames != null)
                {
                    foreach (string groupName in groupNames.Split(','))
                    {
                        List<string> members;
                        if (!groups.TryGetValue(groupName, out members))
                        {
                            members = new List<string>();
                            groups.Add(groupName, members);
                        }

                        members.Add(name);
                    }
                }
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
                    ProcessFeatureOrExtension(feature, includedEnums, includedCommands);
                }
            }

            //==============================================================================
            // Parse extensions
            //==============================================================================

            foreach (XmlNode extension in document.SelectNodes("/registry/extensions/extension"))
            {
                string extensionName = GetAttributeOrNull(extension, "name");
                string api = GetAttributeOrNull(extension, "supported");

                bool isForGL = api.Split('|').Contains("gl");

                if (isForGL && IncludedExtensions.Contains(extensionName))
                {
                    ProcessFeatureOrExtension(extension, includedEnums, includedCommands);
                }
            }

            //==============================================================================
            // Generate C# code
            //==============================================================================

            if (GenerateCSharp)
            {
                using (StreamWriter output = new StreamWriter(Path.Combine(OutputFolder, "GL.cs")))
                {
                    WriteHeaderComment(output, detailed: true);

                    output.WriteLine("using System;");
                    output.WriteLine("using System.Runtime.InteropServices;");
                    output.WriteLine("using System.Text;");
                    output.WriteLine("using SDL2;");
                    output.WriteLine();
                    output.WriteLine("public static class GL");
                    output.WriteLine("{");
                    output.WriteLine("    public delegate void DebugCallback(");
                    output.WriteLine("        GL.GLenum source,");
                    output.WriteLine("        GL.GLenum type,");
                    output.WriteLine("        uint id,");
                    output.WriteLine("        GL.GLenum severity,");
                    output.WriteLine("        int length,");
                    output.WriteLine("        IntPtr message,");
                    output.WriteLine("        IntPtr userParam);");
                    output.WriteLine();

                    //==============================================================================
                    // Generate enum declarations
                    //==============================================================================

                    foreach (var pair in groups)
                    {
                        string name = pair.Key;
                        List<string> members = pair.Value.Intersect(includedEnums).ToList();

                        if (members.Count > 0)
                        {
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

                    if (AllowMissingFunctions)
                    {
                        output.WriteLine("    private static T Load<T>(string name) where T : class");
                        output.WriteLine("    {");
                        output.WriteLine("        IntPtr proc = SDL.SDL_GL_GetProcAddress(name);");
                        output.WriteLine("        if (proc == IntPtr.Zero)");
                        output.WriteLine("        {");
                        output.WriteLine("            return null;");
                        output.WriteLine("        }");
                        output.WriteLine("        return Marshal.GetDelegateForFunctionPointer<T>(proc);");
                        output.WriteLine("    }");
                    }
                    else
                    {
                        output.WriteLine("    private static T Load<T>(string name)");
                        output.WriteLine("    {");
                        output.WriteLine("        IntPtr proc = SDL.SDL_GL_GetProcAddress(name);");
                        output.WriteLine("        if (proc == IntPtr.Zero)");
                        output.WriteLine("        {");
                        output.WriteLine("            throw new GameException(\"Unable to load OpenGL function \" + name);");
                        output.WriteLine("        }");
                        output.WriteLine("        return Marshal.GetDelegateForFunctionPointer<T>(proc);");
                        output.WriteLine("    }");
                    }

                    output.WriteLine();
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

            //==============================================================================
            // Generate C code
            //==============================================================================

            if (GenerateC)
            {
                using (StreamWriter output = new StreamWriter(Path.Combine(OutputFolder, "GL.h")))
                {
                    WriteHeaderComment(output, detailed: true);

                    //==============================================================================
                    // Generate typedefs
                    //==============================================================================

                    output.WriteLine("#include \"khrplatform.h\"");
                    output.WriteLine();

                    foreach (string typedef in typedefs)
                    {
                        output.WriteLine(typedef);
                    }
                    output.WriteLine();

                    //==============================================================================
                    // Generate enum definitions
                    //==============================================================================

                    foreach (string member in includedEnums)
                    {
                        uint value;
                        if (enumValues.TryGetValue(member, out value))
                        {
                            output.WriteLine("#define {0} 0x{1:X8}", member, value);
                        }
                    }
                    output.WriteLine();

                    //==============================================================================
                    // Generate function types
                    //==============================================================================

                    foreach (string commandName in includedCommands)
                    {
                        GLCommand command = commands[commandName];
                        output.Write("typedef {0} (*GLPROC_{1})(", command.ReturnType.Name, command.Name);
                        for (int i = 0; i < command.ParamNames.Count; i++)
                        {
                            string comma = (i < command.ParamNames.Count - 1) ? ", " : "";
                            output.Write("{0} {1}{2}", command.ParamTypes[i].Name, command.ParamNames[i], comma);
                        }
                        output.WriteLine(");");
                    }
                    output.WriteLine();

                    //==============================================================================
                    // Declare pointers to GL functions
                    //==============================================================================

                    foreach (string commandName in includedCommands)
                    {
                        GLCommand command = commands[commandName];
                        output.WriteLine("extern GLPROC_{0} {1}{0};", command.Name, ExtraPrefix);
                    }
                    output.WriteLine();

                    //==============================================================================
                    // Declare the loading function
                    //==============================================================================

                    output.WriteLine("void LoadGL();");
                }

                using (StreamWriter output = new StreamWriter(Path.Combine(OutputFolder, "GL.c")))
                {
                    WriteHeaderComment(output, detailed: false);

                    output.WriteLine("#include <assert.h>");
                    output.WriteLine("#include <SDL2/SDL.h>");
                    output.WriteLine("#include \"GL.h\"");
                    output.WriteLine();

                    //==============================================================================
                    // Define pointers to GL functions
                    //==============================================================================

                    foreach (string commandName in includedCommands)
                    {
                        GLCommand command = commands[commandName];
                        output.WriteLine("GLPROC_{0} {1}{0};", command.Name, ExtraPrefix);
                    }
                    output.WriteLine();

                    //==============================================================================
                    // Generate code to load the GL function pointers
                    //==============================================================================

                    output.WriteLine("static void *Load(const char *name)");
                    output.WriteLine("{");
                    output.WriteLine("    void *proc = SDL_GL_GetProcAddress(name);");
                    output.WriteLine("    assert(proc);");
                    output.WriteLine("    return proc;");
                    output.WriteLine("}");
                    output.WriteLine();
                    output.WriteLine("void LoadGL()");
                    output.WriteLine("{");
                    foreach (string commandName in includedCommands)
                    {
                        GLCommand command = commands[commandName];
                        output.WriteLine("    {1}{0} = (GLPROC_{0})Load(\"{0}\");", command.Name, ExtraPrefix);
                    }
                    output.WriteLine("}");
                }
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

        static void ProcessFeatureOrExtension(XmlNode node, List<string> includedEnums, List<string> includedCommands)
        {
            foreach (XmlNode removeNode in node.SelectNodes("remove"))
            {
                string api = GetAttributeOrNull(removeNode, "api");
                if (api == null || api == "gl")
                {
                    var removedEnums = removeNode.SelectNodes("enum")
                        .Cast<XmlNode>()
                        .Select(x => GetAttributeOrNull(x, "name"));

                    var removedCommands = removeNode.SelectNodes("command")
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
                }
            }

            foreach (XmlNode requireNode in node.SelectNodes("require"))
            {
                string api = GetAttributeOrNull(requireNode, "api");
                if (api == null || api == "gl")
                {
                    var requiredEnums = requireNode.SelectNodes("enum")
                        .Cast<XmlNode>()
                        .Select(x => GetAttributeOrNull(x, "name"));

                    var requiredCommands = requireNode.SelectNodes("command")
                        .Cast<XmlNode>()
                        .Select(x => GetAttributeOrNull(x, "name"));

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
        }

        static string TranslateType(IEnumerable<string> definedGroups, bool isReturnType, GLType type)
        {
            Dictionary<string, string> translations = new Dictionary<string, string>
            {
                { "const GLboolean *", "/*const*/ bool[]" },
                { "const GLbyte *", "/*const*/ sbyte[]" },
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
                { "const void *", "/*const*/ IntPtr" },
                { "const void *const*", "/*const*/ IntPtr" },
                { "GLbitfield", "GLenum" },
                { "GLboolean *", "out bool" },
                { "GLboolean", "bool" },
                { "GLbyte *", "out sbyte" },
                { "GLbyte", "sbyte" },
                { "GLchar *", "StringBuilder" },
                { "GLDEBUGPROC", "[MarshalAs(UnmanagedType.FunctionPtr)] DebugCallback" },
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
                { "GLubyte *", "out byte" },
                { "GLubyte", "byte" },
                { "GLuint *", "out uint" },
                { "GLuint", "uint" },
                { "GLuint64 *", "out ulong" },
                { "GLuint64", "ulong" },
                { "GLushort *", "out ushort" },
                { "GLushort", "ushort" },
                { "void *", "IntPtr" },
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
                "base",
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

        static void WriteHeaderComment(StreamWriter output, bool detailed)
        {
            output.WriteLine("// This file was generated by a tool:");
            output.WriteLine("// https://github.com/holmak/opengl-loader");

            if (detailed)
            {
                output.WriteLine("//");
                output.WriteLine("// GL versions included:");
                foreach (string version in IncludedFeatures)
                {
                    output.WriteLine("//   " + version);
                }
                output.WriteLine("//");
                output.WriteLine("// GL extensions included:");
                foreach (string extension in IncludedExtensions)
                {
                    output.WriteLine("//   " + extension);
                }
            }

            output.WriteLine();
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
