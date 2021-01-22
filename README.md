# error-transformer application
This is a .NET Core 3.1 console application whose purpose is to transform each Interceptor log zip file within a folder into either a more compact zip file (i.e., with only the last day of logs) or into a single text file (containing only the contents of the last day of logs).  This can allow for easier and/or quicker general searching of the Interceptor logs.

## Example
The following is an example of how this application is most often used, transforming the Interceptor log files in an example folder and placing transformed text files into an output folder:
```
error-transformer.exe -i \\wjv-gendfs01\telelogs\2021\01\22 -o c:\error-files\2021\01\22 -u
```

Note that the `-u` command-line parameter causes the application to output a single text file for each error zip file, while the `-i` parameter specifies the folder containing the error zip files to process, and the `-o` parameter specifies the folder in which to save the transformed text files.

Using the `-u` parameter, which outputs the results as a text file, facilitates the use of regular file search tools (such as [FileLocator Lite](https://www.mythicsoft.com/agentransack/)) to search through a large set of the Interceptor log files.  Searching through zip files containing the Interceptor log files is often much more difficult and time-consuming.

Without the `-u` parameter...
```
error-transformer.exe -i \\wjv-gendfs01\telelogs\2021\01\22 -o c:\error-files\2021\01\22
```

...the application would output shortened zip files (containing a subset of the Interceptor log files contained within the original zip file).

## Other command-line parameters
There is also a `-m` command-line parameter that will cause the output to only include the contents of the `interceptor.log` file contained within the Interceptor error zip file.  This will represent only the very latest Interceptor log entries contained with the Interceptor error zip file.

