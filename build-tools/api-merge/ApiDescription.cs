#define KEEP_OLD_WRONG_COMPATIBILITY
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;

using Mono.Options;

namespace Xamarin.Android.ApiMerge {

	class ApiDescription {
		
		public static bool HackGenericTypeParameterNames = true;

		XDocument Contents;
		Dictionary<string, XElement> Types = new Dictionary<string, XElement>();

		public ApiDescription (string source)
		{
			Contents = XDocument.Load (source);
			foreach (var package in Contents.Element ("api").Elements ("package")) {
				AddPackage (package);
			}
		}

		public void Merge (string apiLocation)
		{
#if ADD_OBSOLETE_ON_DISAPPEARANCE
			var filename = Path.GetFileName (apiLocation);
			int apiLevel = int.Parse (filename.Substring (4, filename.IndexOf ('.', 4) - 4));
#endif
			var n = XDocument.Load (apiLocation);
			foreach (var npackage in n.Elements ("api").Elements ("package")) {
				var spackage = GetPackage ((string) npackage.Attribute ("name"));
				if (spackage == null) {
					AddNewPackage (npackage, apiLocation);
					continue;
				}
				foreach (var ntype in npackage.Elements ()) {
					var stype = GetType (spackage, ntype);
					if (stype == null) {
						AddNewType (spackage, ntype, apiLocation);
						continue;
					}
					foreach (var a in ntype.Attributes ()) {
						var sattr = stype.Attribute (a.Name);
						switch (a.Name.LocalName) {
#if KEEP_OLD_WRONG_COMPATIBILITY
						case "extends":
							sattr.Value = a.Value;
							break;
						case "abstract":
							sattr.Value = a.Value;
							break;
						case "deprecated":
							sattr.Value = a.Value;
							break;
						case "final":
							if (a.Value == "false")
								sattr.Value = "false";
							//sattr.Value = a.Value;
							break;
#endif
						default:
							var sa = stype.Attribute (a.Name);
							if (sa == null)
								stype.Add (a);
							else
								sa.SetValue (a.Value);
							break;
						}
					}
#if ADD_OBSOLETE_ON_DISAPPEARANCE
					foreach (var smember in stype.Elements ()) {
						var nmember = GetMember (ntype, smember);
						if (nmember == null) {
							var deprecated = smember.Attribute ("deprecated");
							if (deprecated != null) {
								if (!deprecated.Value.StartsWith ("This member has disappeared"))
									deprecated.Value = "This member has disappeared at API Level " + apiLevel;
							}
							else
								smember.Add (new XAttribute (XName.Get ("deprecated"), "This member has disappeared at API Level " + apiLevel));
						}
					}
#endif
					foreach (var nmember in ntype.Elements ()) {
						var smember = GetMember (stype, nmember);
						if (smember == null) {
							AddNewMember (stype, nmember, apiLocation);
							continue;
						}
						if (nmember.Name.LocalName == "field") {
							// FIXME: enable this to get the latest field attributes precisely.
							/*
							foreach (var a in nmember.Attributes ()) {
								var sa = smember.Attribute (a.Name);
								if (sa == null)
									smember.Add (a);
								else if (sa.Value != a.Value)
									sa.SetValue (a.Value);
							}
							*/
#if KEEP_OLD_WRONG_COMPATIBILITY
							var isDeprecatedS = smember.Attribute ("deprecated");
							var isDeprecatedN = nmember.Attribute ("deprecated");
							if (isDeprecatedS != null && isDeprecatedN != null)
								isDeprecatedS.Value = isDeprecatedN.Value;
#endif
							continue;
						}
						if (nmember.Name.LocalName == "method" || nmember.Name.LocalName == "constructor") {
							smember = GetConstructorOrMethod (stype, nmember);
							if (smember == null) {
								AddNewMember (stype, nmember, apiLocation);
								continue;
							}
							foreach (var a in nmember.Attributes ()) {
								var sa = smember.Attribute (a.Name);
								if (sa == null)
									smember.Add (a);
								else if (sa.Value != a.Value)
									sa.SetValue (a.Value);
							}
#if KEEP_OLD_WRONG_COMPATIBILITY
							var isAbstract = smember.Attribute ("abstract");
							if (isAbstract != null)
								isAbstract.Value = nmember.Attribute ("abstract").Value;
#endif
							// This is required to workaround the
							// issue that API Level 20 does not offer
							// reference docs and we use API Level 21
							// docs instead, but it also removed some
							// members, resulting in missing parameter
							// names (thus p0, p1...).
							// For those members, we preserve older
							// names on the parameters.
							var newNodes = nmember.Elements ("parameter").ToArray ();
							var p = newNodes.FirstOrDefault ();
							if (p != null && p.Attribute ("name").Value == "p0") {
								var oldNodes = smember.Elements ("parameter").ToArray ();
								for (int i = 0; i < newNodes.Length; i++)
									newNodes [i].Attribute ("name").Value = oldNodes [i].Attribute ("name").Value;
							}
							// This is required to alter references to old generic argument name to new one.
							// e.g. Some<T> in old API, Some<A> in new API and droiddoc -> A should be used.
							smember.ReplaceNodes (nmember.Nodes ());
						}
					}
					var tordered = stype.Elements ().OrderBy (e => GetMemberSignature (e)).ToList ();
					stype.RemoveNodes ();
					stype.Add (tordered);
				}
				var pordered = spackage.Elements ().OrderBy (e => GetMemberSignature (e)).ToList ();
				spackage.RemoveNodes ();
				spackage.Add (pordered);
			}
		}

		public void Save (string filename)
		{
			FixupOverrides ();
			if (filename != null)
				Contents.Save (filename);
			else
				Contents.Save (Console.Out);
		}

		void FixupOverrides ()
		{
			foreach (var type in Contents.Elements ("api").Elements ("package").Elements ("class")) {
				foreach (var method in type.Elements ("method")) {
					XElement baseType, sourceType = type;
					string extends;
					while ((extends = (string) sourceType.Attribute ("extends")) != null &&
							Types.TryGetValue (extends, out baseType)) {
						sourceType = baseType;
						var m = GetConstructorOrMethod (sourceType, method);
						if (m == null)
							continue;
						m.Attribute ("final").Value = "false";
					}
				}
			}
		}

		void AddPackage (XElement package)
		{
			foreach (var type in package.Elements ()) {
				AddType (type);
			}
		}

		void AddType (XElement type)
		{
			string package  = (string) type.Parent.Attribute ("name");
			string typeName = (string) type.Attribute ("name");
			string fullName = string.IsNullOrEmpty (package)
				? (string) type.Attribute ("name")
				: package + "." + typeName;

			XElement cur;
			if (Types.TryGetValue (fullName, out cur)) {
				if (!object.ReferenceEquals (cur, type)) {
					Console.Error.WriteLine ("api-merge: warning: Found duplicate type: {0}", fullName);
				}
				return;
			}
			Types.Add (fullName, type);
		}

		XElement AddWithLocation (XElement old, XElement child, string location)
		{
			child.Add (new XAttribute ("merge.SourceFile", location));
			old.Add (child);
			return (XElement) old.LastNode;
		}

		void AddNewPackage (XElement newPackage, string location)
		{
			AddWithLocation (Contents.Element ("api"), newPackage, location);
		}

		void AddNewType (XElement sourcePackage, XElement newType, string location)
		{
			var t = AddWithLocation (sourcePackage, newType, location);
			AddType (t);
		}

		void AddNewMember (XElement sourceType, XElement newMember, string location)
		{
			AddWithLocation (sourceType, newMember, location);
		}

		XElement GetPackage (string package)
		{
			return Contents.Elements ("api").Elements ("package")
				.FirstOrDefault (p => ((string) p.Attribute ("name")) == package);
		}

		XElement GetType (XElement package, XElement type)
		{
			return package.Elements (type.Name)
				.FirstOrDefault (t => ((string) t.Attribute ("name")) == (string) type.Attribute ("name"));
		}

		XElement GetMember (XElement sourceType, XElement newMember)
		{
			string newMemberName = (string) newMember.Attribute ("name");
			return sourceType.Elements (newMember.Name)
				.FirstOrDefault (m => ((string) m.Attribute ("name")) == newMemberName);
		}

		XElement GetConstructorOrMethod (XElement sourceType, XElement newMember)
		{
			string name = (string) newMember.Attribute ("name");
			return sourceType.Elements (newMember.Name).FirstOrDefault (m => CompareMethods (m, newMember, name));
		}

		static bool CompareMethods (XElement sourceMethod, XElement newMethod, string name)
		{
			if (((string) sourceMethod.Attribute ("name")) != name)
				return false;
			if (sourceMethod.Elements ("parameter").Count() != newMethod.Elements ("parameter").Count ())
				return false;
			var sourceTypeMappings  = GetTypeMappings (sourceMethod);
			var newTypeMappings     = GetTypeMappings (newMethod);
			foreach (var arg in sourceMethod.Elements ("parameter").Zip (newMethod.Elements ("parameter"), (o, n) => new KeyValuePair<XElement, XElement> (o, n))) {
				string newType = GetParameterType (arg.Value, newTypeMappings);
				string curType = GetParameterType (arg.Key, sourceTypeMappings);
				if (curType != newType)
					return false;
			}
			return true;
		}

		static Dictionary<string, string> GetTypeMappings (XElement method)
		{
			return method.Parent.Elements ("typeParameters").Elements ("typeParameter")
				.ToDictionary (
						tp => (string) tp.Attribute ("name"),
						tp => tp.Elements ("genericConstraints").Any ()
							? (string) tp.Elements ("genericConstraints").Elements ("genericConstraint").First ().Attribute ("type")
							: "java.lang.Object"
				);
		}

		static string GetParameterType (XElement parameter, Dictionary<string, string> mappings)
		{
			string type = (string) parameter.Attribute ("type");
			return GetParameterType (type, mappings);
		}
		
		const string wildConstraints = "? extends ";
		const string superConstraints = "? super ";
		static readonly char [] type_sep = new char [] {'<', ',', '?'};
		
		static string GetParameterType (string type, Dictionary<string, string> mappings)
		{
			if (HackGenericTypeParameterNames && type.Length == 1) // only generic type parameter (name doesn't matter) or obfuscated type (ignorable)
				return "*";

			// varargs should be normalized.
			if (type.EndsWith ("..."))
				return GetParameterType (type.Substring (0, type.Length - 3) + "[]", mappings);

			int first = type.IndexOfAny (type_sep);
			if (first >= 0 && type [first] == '<') {
				int last = type.LastIndexOf ('>');
				if (last < 0)
					throw new ArgumentException (type);
				return type.Substring (0, first) + type.Substring (last + 1);
			}
			
			if (mappings.ContainsKey (type))
				return mappings [type];
			return type;
		}

		static string GetMemberSignature (XElement e)
		{
			string prefix;
			switch (e.Name.LocalName) {
				case "typeParameters":  prefix = "1|"; break;
				case "implements":      prefix = "2|"; break;
				case "constructor":     prefix = "3|"; break;
				case "method":          prefix = "4|"; break;
				case "field":           prefix = "5|"; break;
				default:                prefix = "0|"; break;
			}
			if (e.Name.LocalName != "method" && e.Name.LocalName != "constructor")
				return prefix + (string) e.Attribute ("name");
			return prefix +
				((string) e.Attribute ("name")) +
				"(" +
				string.Join (", ", e.Elements ("parameter").Select (p => (string) p.Attribute ("type"))) +
				")";
		}
	}
}