using Microsoft.VisualStudio.TestTools.UnitTesting;

// The tests set static switches (the run modes, the poison of collected handles): they run one after the other.
[assembly: DoNotParallelize]
