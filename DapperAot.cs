// Globally enables Dapper.AOT code generation for this assembly. The source
// generator intercepts compatible Dapper call sites (e.g.
// `connection.QueryFirst<T>(sql, params)`) and produces IL that doesn't fall
// back on runtime Reflection.Emit — which is required for NativeAOT.
[assembly: Dapper.DapperAotAttribute(true)]