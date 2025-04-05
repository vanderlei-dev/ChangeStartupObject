using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EnvDTE;
using Microsoft.Build.Evaluation;
using System.IO;

namespace ChangeStartupObject
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class ChangeStartupObjectCommand
    {
        /// <summary>
        /// Command ID.
        /// </summary>
        public const int CommandId = 0x0100;

        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CommandSet = new Guid("b773c0e1-399c-4138-86b3-b711a3c1b64c");

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly AsyncPackage package;

        private readonly MSBuildWorkspace _workspace;

        /// <summary>
        /// Initializes a new instance of the <see cref="ChangeStartupObjectCommand"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        /// <param name="commandService">Command service to add command to, not null.</param>
        private ChangeStartupObjectCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(Execute, menuCommandID);
            commandService.AddCommand(menuItem);            
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static ChangeStartupObjectCommand Instance
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the service provider from the owner package.
        /// </summary>
        private Microsoft.VisualStudio.Shell.IAsyncServiceProvider ServiceProvider
        {
            get
            {
                return this.package;
            }
        }

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            // Switch to the main thread - the call to AddCommand in ChangeStartupObjectCommand's constructor requires
            // the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new ChangeStartupObjectCommand(package, commandService);
        }

        /// <summary>
        /// This function is the callback used to execute the command when the menu item is clicked.
        /// See the constructor to see how the menu item is associated with this function using
        /// OleMenuCommandService service and MenuCommand class.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private void Execute(object sender, EventArgs e)
        {
            _ = package.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                DTE dte = await ChangeStartupObjectPackage.GetDTEAsync();
                EnvDTE.Document activeDocument = dte.ActiveDocument;

                string projectPath = activeDocument.ProjectItem.ContainingProject.FullName;
                string filePath = activeDocument.FullName;                

                string classNamespace = await GetStartupClassAsync(projectPath, filePath);                

                if (!string.IsNullOrEmpty(projectPath) && !string.IsNullOrEmpty(classNamespace))
                {
                    SetStartupObject(projectPath, classNamespace);
                }
            });
        }

        public static void SetStartupObject(string projectPath, string startupObject)
        {
            var project = ProjectCollection.GlobalProjectCollection.LoadedProjects.FirstOrDefault(p => p.FullPath == projectPath);
            if (project == null) 
                project = new Microsoft.Build.Evaluation.Project(projectPath);            

            project.SetProperty("StartupObject", startupObject);
            project.Save();
        }

        public async Task<string> GetStartupClassAsync(string projectPath, string filePath)
        {            
            string sourceCode = File.ReadAllText(filePath);

            // Parse the syntax tree from the file content
            var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);
            var root = await syntaxTree.GetRootAsync();

            // Extract the namespace
            var namespaceDeclaration = root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
            string namespaceName = namespaceDeclaration?.Name.ToString() ?? string.Empty;

            // Find all class declarations
            var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

            foreach (var classDeclaration in classDeclarations)
            {
                // Look for a static method named "Main"
                var mainMethod = classDeclaration.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == "Main" && m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)));

                if (mainMethod != null)
                {
                    string className = classDeclaration.Identifier.Text;
                    return !string.IsNullOrEmpty(namespaceName)
                        ? $"{namespaceName}.{className}"
                        : className; // If no namespace exists, return only the class name
                }
            }

            return null; // No class found with a static "Main" method
        }
    }
}
