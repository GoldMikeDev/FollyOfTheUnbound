' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Namespace Microsoft.CodeAnalysis.Editor.UnitTests.InlineHints
    <Trait(Traits.Feature, Traits.Features.InlineHints)>
    Public NotInheritable Class CSharpInlineRootNamespaceHintsTests
        Inherits AbstractInlineHintsTests

        <WpfFact>
        Public Async Function TestRootNamespaceQualifier() As Task
            Dim input =
            <Workspace>
                <Project Language="C#" CommonReferences="true">
                    <Document>
namespace {|ToolBox:*|}.AddonModules
{
    class Spinner
    {
    }
}
                    </Document>
                </Project>
            </Workspace>

            Await VerifyRootNamespaceHints(input, rootNamespace:="ToolBox")
        End Function

        <WpfFact>
        Public Async Function TestRootNamespaceQualifier_MultiSegment() As Task
            Dim input =
            <Workspace>
                <Project Language="C#" CommonReferences="true">
                    <Document>
namespace {|Company.Product:*|}.AddonModules
{
    class Spinner
    {
    }
}
                    </Document>
                </Project>
            </Workspace>

            Await VerifyRootNamespaceHints(input, rootNamespace:="Company.Product")
        End Function

        <WpfFact>
        Public Async Function TestNoHintWithoutQualifier() As Task
            Dim input =
            <Workspace>
                <Project Language="C#" CommonReferences="true">
                    <Document>
namespace ToolBox.AddonModules
{
    class Spinner
    {
    }
}
                    </Document>
                </Project>
            </Workspace>

            Await VerifyRootNamespaceHints(input, rootNamespace:="ToolBox")
        End Function

        <WpfFact>
        Public Async Function TestFileScopedNamespace() As Task
            Dim input =
            <Workspace>
                <Project Language="C#" CommonReferences="true">
                    <Document>
namespace {|ToolBox:*|}.AddonModules;

class Spinner
{
}
                    </Document>
                </Project>
            </Workspace>

            Await VerifyRootNamespaceHints(input, rootNamespace:="ToolBox")
        End Function
    End Class
End Namespace
