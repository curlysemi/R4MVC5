using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Mvc;
using AreaAttribute = System.Web.Mvc.RouteAreaAttribute;
//using Microsoft.AspNetCore.Mvc;
//using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using R4Mvc.Tools;
using R4Mvc.Tools.CodeGen;
using R4Mvc.Tools.Extensions;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static R4Mvc.Tools.Extensions.SyntaxNodeHelpers;
using System;

namespace R4Mvc.Tools.Services
{
    public class ControllerGeneratorService : IControllerGeneratorService
    {
        private const string ViewNamesClassName = "_ViewNamesClass";

        private readonly Settings _settings;

        public ControllerGeneratorService(Settings settings)
        {
            _settings = settings;
        }

        public string GetControllerArea(INamedTypeSymbol controllerSymbol)
        {
            AttributeData areaAttribute = null;
            var typeSymbol = controllerSymbol;
            while (typeSymbol != null && areaAttribute == null)
            {
                areaAttribute = typeSymbol.GetAttributes().FirstOrDefault(a => a.AttributeClass.InheritsFrom<AreaAttribute>());
                typeSymbol = typeSymbol.BaseType;
            }
            if (areaAttribute == null)
                return string.Empty;

            if (areaAttribute.AttributeClass.ToDisplayString() == typeof(AreaAttribute).FullName)
                return areaAttribute.ConstructorArguments[0].Value?.ToString();

            // parse the constructor to get the area name from derived types
            if (areaAttribute.AttributeClass.BaseType.ToDisplayString() == typeof(AreaAttribute).FullName)
            {
                // direct descendant. Reading the area name from the constructor
                var constructorInit = areaAttribute.AttributeConstructor.DeclaringSyntaxReferences
                    .SelectMany(s => s.SyntaxTree.GetRoot().DescendantNodesAndSelf().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.Text == areaAttribute.AttributeClass.Name))
                    .SelectMany(s => s.DescendantNodesAndSelf().OfType<ConstructorInitializerSyntax>())
                    .First();
                if (constructorInit.ArgumentList.Arguments.Count > 0)
                {
                    var arg = constructorInit.ArgumentList.Arguments[0];
                    if (arg.Expression is LiteralExpressionSyntax litExp)
                    {
                        return litExp.Token.ValueText;
                    }
                }
            }
            return string.Empty;
        }

        private IEnumerable<T> PoorMansDistinctBy<T, U>(IEnumerable<T> collection, Func<T, U> selector) where U : class
        {
            var distinct = new List<T>();
            foreach (var element in collection)
            {
                if (!distinct.Any(m => selector(m) == selector(element)))
                {
                    distinct.Add(element);
                }
            }
            return distinct;
        }

        public ClassDeclarationSyntax GeneratePartialController(ControllerDefinition controller, bool supportsPages)
        {
            // build controller partial class node
            var genControllerClass = new ClassBuilder(controller.Symbol.Name)               // public partial {controllerClass}
                .WithModifiers(SyntaxKind.PublicKeyword, SyntaxKind.PartialKeyword)
                .WithTypeParameters(controller.Symbol.TypeParameters.Select(tp => tp.Name).ToArray()); // optional <T1, T2, �>

            // add a default constructor if there are some but none are zero length
            var gotCustomConstructors = controller.Symbol.Constructors
                .Where(c => c.DeclaredAccessibility == Accessibility.Public)
                .Where(SyntaxNodeHelpers.IsNotR4MVCGenerated)
                .Where(c => !c.IsImplicitlyDeclared)
                .Any();
            if (!gotCustomConstructors)
                /* [GeneratedCode, DebuggerNonUserCode]
                 * public ctor() { }
                 */
                genControllerClass.WithConstructor(c => c
                    .WithModifiers(SyntaxKind.PublicKeyword)
                    .WithGeneratedNonUserCodeAttributes());
            /* [GeneratedCode, DebuggerNonUserCode]
             * public ctor(Dummy d) {}
             */
            genControllerClass.WithConstructor(c => c
                .WithModifiers(SyntaxKind.ProtectedKeyword)
                .WithGeneratedNonUserCodeAttributes()
                .WithParameter("d", Constants.DummyClass));

            AddRedirectMethods(genControllerClass, supportsPages);
            AddParameterlessMethods(genControllerClass, controller.Symbol, controller.IsSecure);

            var actionsExpression = controller.AreaKey != null
                ? _settings.HelpersPrefix + "." + controller.AreaKey + "." + controller.Name
                : _settings.HelpersPrefix + "." + controller.Name;
            var controllerMethods = SyntaxNodeHelpers.GetPublicNonGeneratedControllerMethods(controller.Symbol);
            var controllerMethodNames = controllerMethods.Select(m => m.Name).Distinct().ToArray();
            genControllerClass
                .WithExpressionProperty("Actions", controller.Symbol.Name, actionsExpression, SyntaxKind.PublicKeyword)
                .WithStringField("Area", controller.Area, SyntaxKind.PublicKeyword, SyntaxKind.ReadOnlyKeyword)
                .WithStringField("Name", controller.Name, SyntaxKind.PublicKeyword, SyntaxKind.ReadOnlyKeyword)
                .WithStringField("NameConst", controller.Name, SyntaxKind.PublicKeyword, SyntaxKind.ConstKeyword)
                .WithStaticFieldBackedProperty("ActionNames", "ActionNamesClass", SyntaxKind.PublicKeyword)
                /* [GeneratedCode, DebuggerNonUserCode]
                 * public class ActionNamesClass
                 * {
                 *  public readonly string {action} = "{action}";
                 * }
                 */
                .WithChildClass("ActionNamesClass", ac => ac
                    .WithModifiers(SyntaxKind.PublicKeyword)
                    .WithGeneratedNonUserCodeAttributes()
                    .ForEach(controllerMethodNames, (c, m) => c
                        .WithStringField(m, m, SyntaxKind.PublicKeyword, SyntaxKind.ReadOnlyKeyword)))
                /* [GeneratedCode, DebuggerNonUserCode]
                 * public class ActionNameConstants
                 * {
                 *  public const string {action} = "{action}";
                 * }
                 */
                .WithChildClass("ActionNameConstants", ac => ac
                    .WithModifiers(SyntaxKind.PublicKeyword)
                    .WithGeneratedNonUserCodeAttributes()
                    .ForEach(controllerMethodNames, (c, m) => c
                        .WithStringField(m, m, SyntaxKind.PublicKeyword, SyntaxKind.ConstKeyword)));


            //var distinctControllerMethods = new List<IMethodSymbol>();
            //foreach (var controllerMethod in controllerMethods)
            //{
            //    if (!distinctControllerMethods.Any(m => m.Name == controllerMethod.Name)) {
            //        distinctControllerMethods.Add(controllerMethod);
            //    }
            //}
            var distinctControllerMethods = PoorMansDistinctBy(controllerMethods, m => m.Name);
            foreach (var controllerMethod in distinctControllerMethods)
            {
                var allMethodsOfName = controllerMethods.Where(m => m.Name == controllerMethod.Name);
                var allParams = PoorMansDistinctBy(allMethodsOfName.SelectMany(m => m.Parameters), m => m.Name);

                var paramsClassName = $"ActionParamsClass_{controllerMethod.Name}";
                genControllerClass
                    .WithStaticFieldBackedProperty($"{controllerMethod.Name}Params", paramsClassName, SyntaxKind.PublicKeyword)
                    .WithChildClass(paramsClassName, ac => ac
                        .WithModifiers(SyntaxKind.PublicKeyword)
                        .WithGeneratedNonUserCodeAttributes()
                        .ForEach(allParams, (c, m) => c
                            .WithStringField(SafeName(m.Name), SafeName(m.Name), SyntaxKind.PublicKeyword, SyntaxKind.ReadOnlyKeyword)))
                    ;
            }


            WithViewsClass(genControllerClass, controller.Views);

            return genControllerClass.Build();
        }

        public ClassDeclarationSyntax GenerateR4Controller(ControllerDefinition controller)
        {
            var className = GetR4MVCControllerClassName(controller.Symbol);
            controller.FullyQualifiedR4ClassName = $"{controller.Namespace}.{className}";

            /* [GeneratedCode, DebuggerNonUserCode]
             * public partial class R4MVC_{Controller} : {Controller}
             * {
             *  public R4MVC_{Controller}() : base(Dummy.Instance) {}
             * }
             */
            var r4ControllerClass = new ClassBuilder(className)
                .WithModifiers(SyntaxKind.PublicKeyword, SyntaxKind.PartialKeyword)
                .WithGeneratedNonUserCodeAttributes()
                .WithBaseTypes(controller.Symbol.ContainingNamespace + "." + controller.Symbol.Name)
                .WithConstructor(c => c
                    .WithBaseConstructorCall(IdentifierName(Constants.DummyClass + "." + Constants.DummyClassInstance))
                    .WithModifiers(SyntaxKind.PublicKeyword));
            AddMethodOverrides(r4ControllerClass, controller.Symbol, controller.IsSecure);
            return r4ControllerClass.Build();
        }

        private void AddRedirectMethods(ClassBuilder genControllerClass, bool supportsPages)
        {
            genControllerClass
                /* [GeneratedCode, DebuggerNonUserCode]
                 * protected RedirectToRouteResult RedirectToAction(IActionResult result)
                 * {
                 *  var callInfo = result.GetR4ActionResult();
                 *  return RedirectToRoute(callInfo.RouteValueDictionary);
                 * }
                 */
                .WithMethod("RedirectToAction", "RedirectToRouteResult", m => m
                    .WithModifiers(SyntaxKind.ProtectedKeyword)
                    .WithGeneratedNonUserCodeAttributes()
                    .WithParameter("result", Constants.IActionResultClass)
                    .WithBody(b => b
                        .VariableFromMethodCall("callInfo", "result", Constants.GetR4ActionResult)
                        .ReturnMethodCall(null, "RedirectToRoute", "callInfo.RouteValueDictionary")))

                /* [GeneratedCode, DebuggerNonUserCode]
                 * protected RedirectToRouteResult RedirectToAction(Task<IActionResult> taskResult)
                 * {
                 *  return RedirectToAction(taskResult.Result);
                 * }
                */
                .WithMethod("RedirectToAction", "RedirectToRouteResult", m => m
                    .WithModifiers(SyntaxKind.ProtectedKeyword)
                    .WithGeneratedNonUserCodeAttributes()
                    .WithParameter("taskResult", $"Task<{Constants.IActionResultClass}>")
                    .WithBody(b => b
                        .ReturnMethodCall(null, "RedirectToAction", "taskResult.Result")))

                /* [GeneratedCode, DebuggerNonUserCode]
                 * protected RedirectToRouteResult RedirectToActionPermanent(IActionResult result)
                 * {
                 *  var callInfo = result.GetR4ActionResult();
                 *  return RedirectToRoutePermanent(callInfo.RouteValueDictionary);
                 * }
                 */
                .WithMethod("RedirectToActionPermanent", "RedirectToRouteResult", m => m
                    .WithModifiers(SyntaxKind.ProtectedKeyword)
                    .WithGeneratedNonUserCodeAttributes()
                    .WithParameter("result", Constants.IActionResultClass)
                    .WithBody(b => b
                        .VariableFromMethodCall("callInfo", "result", Constants.GetR4ActionResult)
                        .ReturnMethodCall(null, "RedirectToRoutePermanent", "callInfo.RouteValueDictionary")))

                /* [GeneratedCode, DebuggerNonUserCode]
                 * protected RedirectToRouteResult RedirectToActionPermanent(Task<IActionResult> taskResult)
                 * {
                 *  return RedirectToActionPermanent(taskResult.Result);
                 * }
                */
                .WithMethod("RedirectToActionPermanent", "RedirectToRouteResult", m => m
                    .WithModifiers(SyntaxKind.ProtectedKeyword)
                    .WithGeneratedNonUserCodeAttributes()
                    .WithParameter("taskResult", $"Task<{Constants.IActionResultClass}>")
                    .WithBody(b => b
                        .ReturnMethodCall(null, "RedirectToActionPermanent", "taskResult.Result")));

            if (supportsPages) genControllerClass
                /* [GeneratedCode, DebuggerNonUserCode]
                 * protected RedirectToRouteResult RedirectToPage(IActionResult result)
                 * {
                 *  var callInfo = result.GetR4ActionResult();
                 *  return RedirectToRoute(callInfo.RouteValueDictionary);
                 * }
                 */
                .WithMethod("RedirectToPage", "RedirectToRouteResult", m => m
                    .WithModifiers(SyntaxKind.ProtectedKeyword)
                    .WithGeneratedNonUserCodeAttributes()
                    .WithParameter("result", Constants.IActionResultClass)
                    .WithBody(b => b
                        .VariableFromMethodCall("callInfo", "result", Constants.GetR4ActionResult)
                        .ReturnMethodCall(null, "RedirectToRoute", "callInfo.RouteValueDictionary")))

                /* [GeneratedCode, DebuggerNonUserCode]
                 * protected RedirectToRouteResult RedirectToPage(Task<IActionResult> taskResult)
                 * {
                 *  return RedirectToPage(taskResult.Result);
                 * }
                */
                .WithMethod("RedirectToPage", "RedirectToRouteResult", m => m
                    .WithModifiers(SyntaxKind.ProtectedKeyword)
                    .WithGeneratedNonUserCodeAttributes()
                    .WithParameter("taskResult", $"Task<{Constants.IActionResultClass}>")
                    .WithBody(b => b
                        .ReturnMethodCall(null, "RedirectToPage", "taskResult.Result")))

                /* [GeneratedCode, DebuggerNonUserCode]
                 * protected RedirectToRouteResult RedirectToPagePermanent(IActionResult result)
                 * {
                 *  var callInfo = result.GetR4ActionResult();
                 *  return RedirectToRoutePermanent(callInfo.RouteValueDictionary);
                 * }
                 */
                .WithMethod("RedirectToPagePermanent", "RedirectToRouteResult", m => m
                    .WithModifiers(SyntaxKind.ProtectedKeyword)
                    .WithGeneratedNonUserCodeAttributes()
                    .WithParameter("result", Constants.IActionResultClass)
                    .WithBody(b => b
                        .VariableFromMethodCall("callInfo", "result", Constants.GetR4ActionResult)
                        .ReturnMethodCall(null, "RedirectToRoutePermanent", "callInfo.RouteValueDictionary")))

                /* [GeneratedCode, DebuggerNonUserCode]
                 * protected RedirectToRouteResult RedirectToPagePermanent(Task<IActionResult> taskResult)
                 * {
                 *  return RedirectToPagePermanent(taskResult.Result);
                 * }
                */
                .WithMethod("RedirectToPagePermanent", "RedirectToRouteResult", m => m
                    .WithModifiers(SyntaxKind.ProtectedKeyword)
                    .WithGeneratedNonUserCodeAttributes()
                    .WithParameter("taskResult", $"Task<{Constants.IActionResultClass}>")
                    .WithBody(b => b
                        .ReturnMethodCall(null, "RedirectToPagePermanent", "taskResult.Result")));
        }

        private void AddParameterlessMethods(ClassBuilder genControllerClass, ITypeSymbol mvcSymbol, bool isControllerSecure)
        {
            var methods = mvcSymbol.GetPublicNonGeneratedControllerMethods()
                .GroupBy(m => m.Name)
                .Where(g => !g.Any(m => m.Parameters.Length == 0));
            foreach (var method in methods)
                genControllerClass
                    /* [GeneratedCode, DebuggerNonUserCode]
                     * public virtual IActionResult {method.Key}()
                     * {
                     *  return new R4Mvc_Microsoft_AspNetCore_Mvc_ActionResult(Area, Name, ActionNames.{Action});
                     * }
                     */
                    .WithMethod(method.Key, Constants.IActionResultClass, m => m
                        .WithModifiers(SyntaxKind.PublicKeyword, SyntaxKind.VirtualKeyword)
                        .WithNonActionAttribute()
                        .WithGeneratedNonUserCodeAttributes()
                        .WithBody(b => b
                            .ReturnNewObject(Constants.ActionResultClass,
                                isControllerSecure || method.Any(mg => mg.GetAttributes().Any(a => a.AttributeClass.InheritsFrom<RequireHttpsAttribute>()))
                                    ? new object[] { "Area", "Name", "ActionNames." + method.Key, SimpleLiteral.String("https") }
                                    : new object[] { "Area", "Name", "ActionNames." + method.Key }
                            )));
        }

        private static bool IsCSharpKeyword(string name)
        {
            switch (name)
            {
                case "bool":
                case "byte":
                case "sbyte":
                case "short":
                case "ushort":
                case "int":
                case "uint":
                case "long":
                case "ulong":
                case "double":
                case "float":
                case "decimal":
                case "string":
                case "char":
                case "object":
                case "typeof":
                case "sizeof":
                case "null":
                case "true":
                case "false":
                case "if":
                case "else":
                case "while":
                case "for":
                case "foreach":
                case "do":
                case "switch":
                case "case":
                case "default":
                case "lock":
                case "try":
                case "throw":
                case "catch":
                case "finally":
                case "goto":
                case "break":
                case "continue":
                case "return":
                case "public":
                case "private":
                case "internal":
                case "protected":
                case "static":
                case "readonly":
                case "sealed":
                case "const":
                case "new":
                case "override":
                case "abstract":
                case "virtual":
                case "partial":
                case "ref":
                case "out":
                case "in":
                case "where":
                case "params":
                case "this":
                case "base":
                case "namespace":
                case "using":
                case "class":
                case "struct":
                case "interface":
                case "delegate":
                case "checked":
                case "get":
                case "set":
                case "add":
                case "remove":
                case "operator":
                case "implicit":
                case "explicit":
                case "fixed":
                case "extern":
                case "event":
                case "enum":
                case "unsafe":
                    return true;
                default:
                    return false;
            }
        }

        private string SafeName(string name)
        {
            return IsCSharpKeyword(name) ? $"@{name}" : name;
        } 

        private void AddMethodOverrides(ClassBuilder classBuilder, ITypeSymbol mvcSymbol, bool isControllerSecure)
        {
            const string overrideMethodSuffix = "Override";
            foreach (var method in mvcSymbol.GetPublicNonGeneratedControllerMethods())
            {
                var methodReturnType = method.ReturnType;
                bool isTaskResult = false, isGenericTaskResult = false;
                if (methodReturnType.InheritsFrom<Task>())
                {
                    isTaskResult = true;
                    var taskReturnType = methodReturnType as INamedTypeSymbol;
                    if (taskReturnType.TypeArguments.Length > 0)
                    {
                        methodReturnType = taskReturnType.TypeArguments[0];
                        isGenericTaskResult = true;
                    }
                }

                var callInfoType = Constants.ActionResultClass;
                if (methodReturnType.InheritsFrom<JsonResult>())
                    callInfoType = Constants.JsonResultClass;
                else if (methodReturnType.InheritsFrom<ContentResult>())
                    callInfoType = Constants.ContentResultClass;
                else if (methodReturnType.InheritsFrom<FileResult>())
                    callInfoType = Constants.FileResultClass;
                else if (methodReturnType.InheritsFrom<ViewResult>())
                    callInfoType = Constants.ViewResultClass;
                else if (methodReturnType.InheritsFrom<PartialViewResult>())
                    callInfoType = Constants.PartialViewResultClass;
                else if (methodReturnType.InheritsFrom<RedirectResult>())
                    callInfoType = Constants.RedirectResultClass;
                //else if (methodReturnType.InheritsFrom<RedirectToActionResult>())
                //    callInfoType = Constants.RedirectToActionResultClass;
                else if (methodReturnType.InheritsFrom<RedirectToRouteResult>())
                    callInfoType = Constants.RedirectToRouteResultClass;
                //else if (methodReturnType.InheritsFrom<IConvertToActionResult>())
                //    callInfoType = Constants.ActionResultClass;
                else if ((!isTaskResult || isGenericTaskResult) && !methodReturnType.InheritsFrom<ActionResult>())
                {
                    // Not a return type we support right now. Returning
                    continue;
                }

                classBuilder
                    /* [NonAction]
                     * partial void {action}Override({ActionResultType} callInfo, [� params]);
                     */
                    .WithMethod(method.Name + overrideMethodSuffix, null, m => m
                        .WithModifiers(SyntaxKind.PartialKeyword)
                        .WithNonActionAttribute()
                        .WithParameter("callInfo", callInfoType)
                        .ForEach(method.Parameters, (m2, p) => m2
                            .WithParameter(SafeName(p.Name), p.Type.ToString()))
                        .WithNoBody())
                    /* [NonAction]
                     * public overrive {ActionResultType} {action}([� params])
                     * {
                     *  var callInfo = new R4Mvc_Microsoft_AspNetCore_Mvc_ActionResult(Area, Name, ActionNames.{Action});
                     *  ModelUnbinderHelpers.AddRouteValues(callInfo.RouteValueDictionary, "paramName", paramName);
                     *  {Action}Override(callInfo, {parameters});
                     *  return callInfo;
                     * }
                     */
                    .WithMethod(method.Name, method.ReturnType.ToString(), m => m
                        .WithModifiers(SyntaxKind.PublicKeyword, SyntaxKind.OverrideKeyword)
                        .WithNonActionAttribute()
                        .ForEach(method.Parameters, (m2, p) => m2
                            .WithParameter(SafeName(p.Name), p.Type.ToString()))
                        .WithBody(b => b
                            .VariableFromNewObject("callInfo", callInfoType,
                                isControllerSecure || method.GetAttributes().Any(a => a.AttributeClass.InheritsFrom<RequireHttpsAttribute>())
                                    ? new object[] { "Area", "Name", "ActionNames." + method.Name, SimpleLiteral.String("https") }
                                    : new object[] { "Area", "Name", "ActionNames." + method.Name }
                            )
                            .ForEach(method.Parameters, (cb, p) => cb
                                .MethodCall("ModelUnbinderHelpers", "AddRouteValues", "callInfo.RouteValueDictionary", SimpleLiteral.String(SafeName(p.Name)), SafeName(p.Name)))
                            .MethodCall(null, method.Name + overrideMethodSuffix, new[] { "callInfo" }.Concat(method.Parameters.Select(p => SafeName(p.Name))).ToArray())
                            .Statement(rb => isTaskResult
                                ? rb.ReturnMethodCall(typeof(Task).FullName, "FromResult" + (isGenericTaskResult ? "<" + methodReturnType + ">" : null), "callInfo")
                                : rb.ReturnVariable("callInfo"))
                        ));
            }
        }

        internal static string GetR4MVCControllerClassName(INamedTypeSymbol controllerClass)
        {
            return $"{Constants.PrefixName}_{controllerClass.Name}";
        }

        public ClassBuilder WithViewsClass(ClassBuilder classBuilder, IEnumerable<View> viewFiles)
        {
            var viewEditorTemplates = viewFiles.Where(c => c.TemplateKind == "EditorTemplates" || c.TemplateKind == "DisplayTemplates");
            var subpathViews = viewFiles.Where(c => c.TemplateKind != null && c.TemplateKind != "EditorTemplates" && c.TemplateKind != "DisplayTemplates")
                .OrderBy(v => v.TemplateKind);

            /* public class ViewsClass
             * {
             * [...] */
             classBuilder.WithChildClass("ViewsClass", cb => cb
                .WithModifiers(SyntaxKind.PublicKeyword)
                .WithGeneratedNonUserCodeAttributes()
                // static readonly _ViewNamesClass s_ViewNames = new _ViewNamesClass();
                // public _ViewNamesClass ViewNames => s_ViewNames;
                .WithStaticFieldBackedProperty("ViewNames", ViewNamesClassName, SyntaxKind.PublicKeyword)
                /* public class _ViewNamesClass
                 * {
                 *  public readonly string {view} = "{view}";
                 * }
                 */
                .WithChildClass(ViewNamesClassName, vnc => vnc
                    .WithModifiers(SyntaxKind.PublicKeyword)
                    .ForEach(viewFiles.Where(c => c.TemplateKind == null), (vc, v) => vc
                        .WithStringField(v.Name.SanitiseFieldName(), v.Name, SyntaxKind.PublicKeyword, SyntaxKind.ReadOnlyKeyword)))
                .ForEach(viewFiles.Where(c => c.TemplateKind == null), (c, v) => c
                    // public readonly string {view} = "~/Views/{controller}/{view}.cshtml";
                    .WithStringField(v.Name.SanitiseFieldName(), v.RelativePath.ToString(), SyntaxKind.PublicKeyword, SyntaxKind.ReadOnlyKeyword))
                .ForEach(viewEditorTemplates.GroupBy(v => v.TemplateKind), (c, g) => c
                    // static readonly _DisplayTemplatesClass s_DisplayTemplates = new _DisplayTemplatesClass();
                    // public _DisplayTemplatesClass DisplayTemplates => s_DisplayTemplates;
                    .WithStaticFieldBackedProperty(g.Key, $"_{g.Key}Class", SyntaxKind.PublicKeyword)
                    /* public partial _DisplayTemplatesClass
                     * {
                     *  public readonly string {view} = "{view}";
                     * }
                     */
                    .WithChildClass($"_{g.Key}Class", tc => tc
                        .WithModifiers(SyntaxKind.PublicKeyword, SyntaxKind.PartialKeyword)
                        .ForEach(g, (tcc, v) => tcc
                            .WithStringField(v.Name.SanitiseFieldName(), v.Name, SyntaxKind.PublicKeyword, SyntaxKind.ReadOnlyKeyword))))
                .ForEach(subpathViews.GroupBy(v => v.TemplateKind), (c, g) => c
                    // static readonly _{viewFolder}Class s_{viewFolder} = new _{viewFolder}Class();
                    // public _{viewFolder}Class {viewFolder} => s_{viewFolder};
                    .WithStaticFieldBackedProperty(g.Key, $"_{g.Key}Class", SyntaxKind.PublicKeyword)
                    /* public class _{viewFolder}Class
                     * {
                     * [...] */
                    .WithChildClass($"_{g.Key}Class", tc => tc
                        .WithModifiers(SyntaxKind.PublicKeyword, SyntaxKind.PartialKeyword)
                        // static readonly _ViewNamesClass s_ViewNames = new _ViewNamesClass();
                        // public _ViewNamesClass ViewNames => s_ViewNames;
                        .WithStaticFieldBackedProperty("ViewNames", ViewNamesClassName, SyntaxKind.PublicKeyword)
                        /* public class _ViewNamesClass
                         * {
                         *  public readonly string {view} = "{view}";
                         * }
                         */
                        .WithChildClass(ViewNamesClassName, vnc => vnc
                            .WithModifiers(SyntaxKind.PublicKeyword)
                            .ForEach(g, (vc, v) => vc
                                .WithStringField(v.Name.SanitiseFieldName(), v.Name, SyntaxKind.PublicKeyword, SyntaxKind.ReadOnlyKeyword)))
                        .ForEach(g, (vc, v) => vc
                            // public string {view} = "~/Views/{controller}/{viewFolder}/{view}.cshtml";
                            .WithStringField(v.Name.SanitiseFieldName(), v.RelativePath.ToString(), SyntaxKind.PublicKeyword, SyntaxKind.ReadOnlyKeyword)))));

            return classBuilder
                .WithStaticFieldBackedProperty("Views", "ViewsClass", SyntaxKind.PublicKeyword);
        }
    }
}
