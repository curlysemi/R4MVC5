using System;
using System.Collections.Generic;
using System.Linq;
using R4Mvc.Tools.Extensions;
using Path = System.IO.Path;

namespace R4Mvc.Tools.Locators
{
    public class DefaultRazorViewLocator : IViewLocator
    {
        protected const string ViewsFolder = "Views";
        protected const string AreasFolder = "Areas";

        private readonly IFileLocator _fileLocator;
        private readonly Settings _settings;

        public DefaultRazorViewLocator(IFileLocator fileLocator, Settings settings)
        {
            _fileLocator = fileLocator;
            _settings = settings;
        }

        protected virtual string GetViewsRoot(string projectRoot) => Path.Combine(projectRoot, ViewsFolder);
        protected virtual string GetAreaViewsRoot(string areaRoot, string areaName) => Path.Combine(areaRoot, ViewsFolder);

        public virtual IEnumerable<View> Find(string projectRoot)
        {
            foreach (var (Area, Controller, Path) in FindControllerViewFolders(projectRoot))
            {
                if (!_fileLocator.DirectoryExists(Path))
                    continue;
                foreach (var view in FindViews(projectRoot, Area, Controller, Path))
                    yield return view;
            }
        }

        protected IEnumerable<(string Area, string Controller, string Path)> FindControllerViewFolders(string projectRoot)
        {
            var viewsRoot = GetViewsRoot(projectRoot);
            if (_fileLocator.DirectoryExists(viewsRoot))
                foreach (var controllerPath in _fileLocator.GetDirectories(viewsRoot))
                {
                    var controllerName = Path.GetFileName(controllerPath);
                    yield return (string.Empty, controllerName, controllerPath);
                }

            var areasPath = Path.Combine(projectRoot, AreasFolder);
            if (_fileLocator.DirectoryExists(areasPath))
                foreach (var areaRoot in _fileLocator.GetDirectories(areasPath))
                {
                    var areaName = Path.GetFileName(areaRoot);
                    viewsRoot = GetAreaViewsRoot(areaRoot, areaName);
                    if (_fileLocator.DirectoryExists(viewsRoot))
                        foreach (var controllerPath in _fileLocator.GetDirectories(viewsRoot))
                        {
                            var controllerName = Path.GetFileName(controllerPath);
                            yield return (areaName, controllerName, controllerPath);
                        }
                }
        }

        protected virtual IEnumerable<View> FindViews(string projectRoot, string areaName, string controllerName, string controllerPath)
        {
            return GetViewsForSubDirectory(projectRoot, controllerName, areaName, controllerPath);
        }

        private IEnumerable<View> GetViewsForSubDirectory(string projectRoot, string controllerName, string areaName, string directory)
        {
            var views = new List<View>();
            foreach (var file in _fileLocator.GetFiles(directory, "*.cshtml"))
            {
                views.Add(GetView(projectRoot, file, controllerName, areaName));
            }

            string searchPath = directory;

            Dictionary<string, IEnumerable<View>> subViews = null;
            if (searchPath != null)
            {
                subViews = GetSubViews(projectRoot, controllerName, areaName, searchPath);
                if (subViews?.Any() == true)
                {
                    var relativePath = new Uri("~" + searchPath.GetRelativePath(projectRoot).Replace("\\", "/"), UriKind.Relative);

                    var directoryName = Path.GetFileName(searchPath);

                    views.Add(new View(areaName, controllerName, searchPath, relativePath, directoryName, subViews));
                }
            }

            return views;
        }

        private Dictionary<string, IEnumerable<View>> GetSubViews(string projectRoot, string controllerName, string areaName, string path)
        {
            var subViews = new Dictionary<string, IEnumerable<View>>();

            foreach (var directory in _fileLocator.GetDirectories(path))
            {
                var directoryName = Path.GetFileName(directory);

                subViews[directoryName] = GetViewsForSubDirectory(projectRoot, controllerName, areaName, directory);
            }

            return subViews;
        }

        private View GetView(string projectRoot, string filePath, string controllerName, string areaName, string templateKind = null, string searchPath = null)
        {
            var relativePath = new Uri("~" + filePath.GetRelativePath(projectRoot).Replace("\\", "/"), UriKind.Relative);
            var templateKindSegment = templateKind != null ? templateKind + "/" : null;

            return new View(areaName, controllerName, Path.GetFileNameWithoutExtension(filePath), relativePath, templateKind, null);
        }
    }
}
