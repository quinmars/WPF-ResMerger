using ResMerger;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Xml;
using System.Xml.Linq;

/*
The MIT License (MIT)

Copyright (c) 2013 - ERGOSIGN http://www.ergosign.de

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/
namespace ResMerger
{
    /// <summary>
    /// <para>
    /// The class Data represents a container for Documents and their DependencyCount
    /// </para>
    /// </summary>
    public class Data
    {
        public XDocument Document { get; set; }
        public int DependencyCount { get; set; }

        public Data(XDocument document, int dependencyCount)
        {
            Document = document;
            DependencyCount = dependencyCount;
        }
    }

    /// <summary>
    /// <para>
    /// The class ResMerge copies all resource dictionary entries into one large file respecting the dependencies to increase performance.
    /// </para>
    /// </summary>
    public static class ResourceMerger
    {
        /// <summary>
        /// save all resources into one big resource dictionary respecting the dependencies to increase performance
        /// </summary>
        /// <param name="projectPath">project path (C:/..)</param>
        /// <param name="relativeSourceFilePath">relative source file path (/LookAndFeel.xaml)</param>
        /// <param name="relativeOutputFilePath">relative output file path (/xGeneric/Generic.xaml)</param>
        /// <param name="projectName">project name</param>
        /// <param name="resDictString">resource dictionary string (node name)</param>
        public static void MergeResources(string projectPath, string projectName = null, string relativeSourceFilePath = "/LookAndFeel.xaml", string relativeOutputFilePath = "/FullLookAndFeel.xaml")
        {
            // if project path does not exist throw exception
            if (!Directory.Exists(projectPath))
                throw new DirectoryNotFoundException($"Project path `{projectPath}` does not exist.");

            // Get default values for optional parameters
            projectName = string.IsNullOrEmpty(projectName) ? Path.GetFileName(Path.GetDirectoryName(projectPath)) : projectName;

            // if relativeSourceFilePath is not of type .xaml throw exception
            if (!relativeSourceFilePath.EndsWith(".xaml", StringComparison.InvariantCultureIgnoreCase))
                throw new InvalidOperationException($"The relative source file `{relativeSourceFilePath}` should have a .xaml extension.");

            // if relativeOutputFilePath is not of type .xaml throw exception
            if (!relativeOutputFilePath.EndsWith(".xaml", StringComparison.InvariantCultureIgnoreCase))
                throw new InvalidOperationException($"The relative output file `{relativeOutputFilePath}` should have a .xaml extension.");

            // create sourceFilePath
            var sourceFilePath = projectPath + relativeSourceFilePath;

            // if source file does not exist throw exception
            if (!File.Exists(sourceFilePath))
                throw new FileNotFoundException($"The source file `{sourceFilePath}` was not found.");

            // load source doc
            var sourceDoc = XDocument.Load(sourceFilePath);

            // get default namespace for doc creation and filtering
            var defaultNameSpace = sourceDoc.Root.GetDefaultNamespace();

            // get res dict string
            var resDictString = sourceDoc.Root.Name.LocalName;

            // create output doc
            var outputDoc = XDocument.Parse("<" + resDictString + " xmlns=\"" + defaultNameSpace + "\"/>");

            // create documents
            var documents = new Dictionary<string, Data>();

            // create dictionary for references that cannot be resolved locally.
            var unresolvedRefs = new HashSet<string>();

            // add elements
            ResourceMerger.PrepareDocuments(ref documents, unresolvedRefs, projectPath, projectName, relativeSourceFilePath);

            // add referenced elements
            if (unresolvedRefs.Any())
            {
                var xnsp = outputDoc.Root.GetDefaultNamespace();

                var mergedDicts = unresolvedRefs
                    .Select(x => new XElement(xnsp + resDictString, new XAttribute("Source", x)))
                    .ToArray();

                var elm = new XElement(xnsp + resDictString + ".MergedDictionaries", mergedDicts);
                outputDoc.Root.Add(elm);
            }

            // add elements (ordered by dependency count)
            foreach (var item in documents.OrderByDescending(item => item.Value.DependencyCount))
            {
                // add attributes
                foreach (var attribute in item.Value.Document.Root.Attributes())
                    outputDoc.Root.SetAttributeValue(attribute.Name, attribute.Value);

                // add elements
                outputDoc.Root.Add(item.Value.Document.Root.Elements().Where(e => !e.Name.LocalName.StartsWith(resDictString)));
            }

            using (var ms = new MemoryStream())
            {
                outputDoc.Save(ms);

                if (OutputEqualsExistingFileContent(Path.Combine(projectPath, relativeOutputFilePath), ms.ToArray()))
                    return;
            }

            // save file
            outputDoc.Save(projectPath + relativeOutputFilePath);
        }

        private static bool OutputEqualsExistingFileContent(string targetFileName, IEnumerable<byte> newFileContent)
        {
            var normalizedFileName = targetFileName.Replace("/", "\\");

            // don't check for equality if the FullLookAndFeel.xaml doesn't exist already
            if (!File.Exists(normalizedFileName))
                return false;

            var existingFileContentBytes = File.ReadAllBytes(normalizedFileName);
            return newFileContent.SequenceEqual(existingFileContentBytes); ;
        }

        /// <summary>
        /// Get a collection of resource dictionary source paths respecting the dependencies 
        /// </summary>
        /// <param name="documents">output document collection</param>
        /// <param name="projectPath">project path</param>
        /// <param name="unresolvedRefs">references that cannot be resolved locally</param>
        /// <param name="projectName">project name</param>
        /// <param name="relativeSourceFilePath">relative source file path</param>
        /// <param name="resDictString">resource dictionary string (node name)</param>
        /// <param name="firstTime">first time, is LookAndFeel?</param>
        /// <param name="parentDependencyCount">dependency count</param>
        private static void PrepareDocuments(ref Dictionary<string, Data> documents, ISet<string> unresolvedRefs, string projectPath, string projectName, string relativeSourceFilePath, bool firstTime = true, int parentDependencyCount = 0)
        {
            // load current doc
            var absoluteSourceFilePath = projectPath + relativeSourceFilePath;

            // if file does not exist throw exception
            if (!File.Exists(absoluteSourceFilePath))
                new FileNotFoundException($"The source file `{absoluteSourceFilePath}` was not found.");

            // load the doc
            var doc = XDocument.Load(absoluteSourceFilePath);

            // get the corresponding res dict name
            var resDictString = doc.Root.Name.LocalName;

            // get default namespace
            var defaultNameSpace = doc.Root.GetDefaultNamespace();

            // if key already added increase dependency count else add item with dependency count set to 0
            if (documents.ContainsKey(absoluteSourceFilePath))
                documents[absoluteSourceFilePath].DependencyCount = Math.Max(documents[absoluteSourceFilePath].DependencyCount + 1, parentDependencyCount + 1);
            else
                documents.Add(absoluteSourceFilePath, new Data(doc, firstTime ? -1 : parentDependencyCount + 1));

            // call PrepareDocuments() for each merged dictionary
            foreach (var dict in doc.Root.Descendants(defaultNameSpace + resDictString))
            {
                var relPath = dict.Attribute("Source").Value.Replace("/" + projectName + ";component/", string.Empty);
                if (!File.Exists(projectPath + relPath))
                    unresolvedRefs.Add(relPath);
                else
                    PrepareDocuments(ref documents, unresolvedRefs, projectPath, projectName, relPath, false, documents[absoluteSourceFilePath].DependencyCount);

            }
        }
    }
}
