using System;

namespace Android {

	public abstract class ReferenceFilesAttribute : Attribute
	{
		internal ReferenceFilesAttribute () {}

		public string	EmbeddedArchive     {get; set;}
		public string   PackageName         {get; set;}
		public string   InstallInstructions {get; set;}
		public string   SourceUrl           {get; set;}
		public string   Version             {get; set;}
		public string   Sha1sum             {get; set;}
	}
}