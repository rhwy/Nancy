﻿namespace Nancy.Routing
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Diagnostics;
    using Responses.Negotiation;
    using RouteCandidate = System.Tuple<string, int, RouteDescription, IRoutePatternMatchResult>;
    using ResolveResult = System.Tuple<Route, DynamicDictionary, System.Func<NancyContext, Response>, System.Action<NancyContext>, System.Func<NancyContext, System.Exception, Response>>;

    /// <summary>
    /// The default implementation for deciding if any of the available routes is a match for the incoming HTTP request.
    /// </summary>
    public class DefaultRouteResolver : IRouteResolver, IDiagnosticsProvider
    {
        private readonly INancyModuleCatalog nancyModuleCatalog;
        private readonly IRoutePatternMatcher routePatternMatcher;
        private readonly INancyModuleBuilder moduleBuilder;
        private readonly IRouteCache cache;
        private readonly IEnumerable<IResponseProcessor> responseProcessors;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultRouteResolver"/> class.
        /// </summary>
        /// <param name="nancyModuleCatalog">The module catalog that modules should be</param>
        /// <param name="routePatternMatcher">The route pattern matcher that should be used to verify if the route is a match to any of the registered routes.</param>
        /// <param name="moduleBuilder">The module builder that will make sure that the resolved module is full configured.</param>
        /// <param name="cache">The route cache that should be used to resolve modules from.</param>
        /// <param name="responseProcessors"></param>
        public DefaultRouteResolver(INancyModuleCatalog nancyModuleCatalog, IRoutePatternMatcher routePatternMatcher, INancyModuleBuilder moduleBuilder, IRouteCache cache, IEnumerable<IResponseProcessor> responseProcessors)
        {
            this.nancyModuleCatalog = nancyModuleCatalog;
            this.routePatternMatcher = routePatternMatcher;
            this.moduleBuilder = moduleBuilder;
            this.cache = cache;
            this.responseProcessors = responseProcessors;
        }

        /// <summary>
        /// Gets the route, and the corresponding parameter dictionary from the URL
        /// </summary>
        /// <param name="context">Current context</param>
        /// <returns>Tuple - Item1 being the Route, Item2 being the parameters dictionary, Item3 being the prereq, Item4 being the postreq, Item5 being the error handler</returns>
        public ResolveResult Resolve(NancyContext context)
        {
            var result =
                this.Resolve(context.Request.Path, context, this.cache);

            return result.Selected;
        }

        private ResolveResult CreateRouteAndParametersFromMatch(NancyContext context, RouteCandidate routeMatchToReturn)
        {
            var associatedModule =
                this.GetInitializedModuleForMatch(context, routeMatchToReturn);

            var route = associatedModule.Routes.ElementAt(routeMatchToReturn.Item2);

            return new ResolveResult(route, routeMatchToReturn.Item4.Parameters, associatedModule.Before, associatedModule.After, associatedModule.OnError);
        }

        private NancyModule GetInitializedModuleForMatch(NancyContext context, RouteCandidate routeMatchToReturn)
        {
            var module =
                this.nancyModuleCatalog.GetModuleByKey(routeMatchToReturn.Item1, context);

            return this.moduleBuilder.BuildModule(module, context);
        }

        private static IEnumerable<RouteCandidate> GetTopRouteMatchesNew(Tuple<List<RouteCandidate>, Dictionary<string, List<RouteCandidate>>> routes)
        {
            var maxSegments = 0;
            var maxParameters = 0;
            
            // Order is by number of path segment matches first number of parameter matches second.  
            // If two candidates have the same number of path segments the tie breaker is the parameter count.
            var selectedRoutes = routes.Item1
                .OrderBy(x => x.Item4.Parameters.GetDynamicMemberNames().Count())
                .OrderByDescending(x => x.Item3.Path.Count(c => c.Equals('/')));

            foreach (var tuple in selectedRoutes)
            {
                var segments = 
                    tuple.Item3.Path.Count(c => c == '/');
                
                var parameters = 
                    tuple.Item4.Parameters.GetDynamicMemberNames().Count();
                
                if (segments < maxSegments || parameters < maxParameters)
                {
                    yield break;
                }

                maxSegments = segments;
                maxParameters = parameters;
                
                yield return tuple;
            }
        }

        private ResolveResults Resolve(string path, NancyContext context, IRouteCache routeCache)
        {
            if (routeCache.IsEmpty())
            {
                context.Trace.TraceLog.WriteLog(s => s.AppendLine("[DefaultRouteResolver] No routes available"));
                return new ResolveResults
                {
                    Selected = new ResolveResult(new NotFoundRoute(context.Request.Method, path), DynamicDictionary.Empty, null, null, null)
                };
            }
            
            var routes =
                routeCache.GetRouteCandidates();

            // Condition
            routes =
                routes.Filter(context, "Invalid condition", (ctx, route) =>{
                    var validCondition =
                        ((route.Item3.Condition == null) || (route.Item3.Condition(ctx)));

                    return new Tuple<bool, RouteCandidate>(
                        validCondition,
                        route
                    );
                });

            if (!routes.Item1.Any())
            {
                context.Trace.TraceLog.WriteLog(s => s.AppendLine("[DefaultRouteResolver] No route had a valid condition"));
                return new ResolveResults
                {
                    Selected = new ResolveResult(new NotFoundRoute(context.Request.Method, path), DynamicDictionary.Empty, null, null, null),
                    Rejected = routes.Item2
                };
            }

            // Path
            routes =
                routes.Filter(context, "Path did not match", (ctx, route) => {
                    var validationResult = 
                        this.routePatternMatcher.Match(path, route.Item3.Path, route.Item3.Segments, context);

                    var routeToReturn =
                        (validationResult.IsMatch) ? new RouteCandidate(route.Item1, route.Item2, route.Item3, validationResult) : route;

                    return new Tuple<bool, RouteCandidate>(
                        validationResult.IsMatch,
                        routeToReturn
                    );
                });

            if (!routes.Item1.Any())
            {
                context.Trace.TraceLog.WriteLog(s => s.AppendLine("[DefaultRouteResolver] No route matched the requested path"));
                return new ResolveResults
                {
                    Selected = new ResolveResult(new NotFoundRoute(context.Request.Method, path), DynamicDictionary.Empty, null, null, null),
                    Rejected = routes.Item2
                };
            }

            // Method
            routes =
                routes.Filter(context, "Request method did not match", (ctx, route) =>{
                    var routeMethod = 
                        route.Item3.Method.ToUpperInvariant();

                    var requestMethod = 
                        ctx.Request.Method.ToUpperInvariant();
                    
                    var methodIsValid =
                        routeMethod.Equals(requestMethod) || (routeMethod.Equals("GET") && requestMethod.Equals("HEAD"));

                    return new Tuple<bool, RouteCandidate>(
                        methodIsValid,
                        route
                    );
                });

            if (!routes.Item1.Any())
            {
                var allowedMethods = routes.Item2.Values.SelectMany(x => x.Select(y => y.Item3.Method)).Distinct();
                if (context.Request.Method.Equals("OPTIONS"))
                {
                    return new ResolveResults
                    {
                        Selected = new ResolveResult(new OptionsRoute(context.Request.Path, allowedMethods), DynamicDictionary.Empty, null, null, null),
                        Rejected = routes.Item2
                    };
                }
                context.Trace.TraceLog.WriteLog(s => s.AppendLine("[DefaultRouteResolver] Route Matched But Method Not Allowed"));
                return new ResolveResults
                {
                    Selected = new ResolveResult(new MethodNotAllowedRoute(path, context.Request.Method, allowedMethods), DynamicDictionary.Empty, null, null, null),
                    Rejected = routes.Item2
                };
            }

            // Exact match
            var exactMatchResults =
                routes.Filter(context, "No exact match", (ctx, route) =>{
                    var routeIsExactMatch =
                        !route.Item4.Parameters.GetDynamicMemberNames().Any();

                    return new Tuple<bool, RouteCandidate>(
                        routeIsExactMatch,
                        route
                    );
                });

            if (exactMatchResults.Item1.Any())
            {
                context.Trace.TraceLog.WriteLog(s => s.AppendLine("[DefaultRouteResolver] Found exact match route"));
                return new ResolveResults
                {
                    Selected = this.CreateRouteAndParametersFromMatch(context, exactMatchResults.Item1.First()),
                    Rejected = exactMatchResults.Item2
                };
            }

            // First match out of multiple candidates
            var selected =
                GetTopRouteMatchesNew(routes).First();

            context.Trace.TraceLog.WriteLog(s => s.AppendLine("[DefaultRouteResolver] Selected best match"));
            return new ResolveResults
            {
                Selected = this.CreateRouteAndParametersFromMatch(context, selected),
                Rejected = exactMatchResults.Item2
            };
        }

        /// <summary>
        /// Used internally by the <see cref="DefaultRouteResolver"/> to store information about the routes that were
        /// rejected during route resolution. The information is used by diagnostics to provide insight into why routes
        /// where rejected.
        /// </summary>
        private class ResolveResults
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="ResolveResults"/> class.
            /// </summary>
            public ResolveResults()
            {
                this.Rejected = new Dictionary<string, List<RouteCandidate>>();
            }

            /// <summary>
            /// The route that was selected as the most suitable match for the current request.
            /// </summary>
            /// <value></value>
            public ResolveResult Selected { get; set; }

            /// <summary>
            /// The routes (value) and reason (key) that were rejected during route resolution.
            /// </summary>
            public Dictionary<string, List<RouteCandidate>> Rejected { get; set; }
        }

        /// <summary>
        /// Gets the name of the provider.
        /// </summary>
        /// <value>A <see cref="string"/> containing the name of the provider.</value>
        public string Name
        {
            get { return "Default route resolver"; }
        }

        /// <summary>
        /// Gets the description of the provider.
        /// </summary>
        /// <value>A <see cref="string"/> containing the description of the provider.</value>
        public string Description
        {
            get { return "A description"; }
        }

        /// <summary>
        /// Gets the object that contains the interactive diagnostics methods.
        /// </summary>
        /// <value>An instance of the interactive diagnostics object.</value>
        public object DiagnosticObject
        {
            get { return new DefaultRouteResolverDiagnosticsProvider(this); }
        }

        public class DefaultRouteResolverDiagnosticsProvider
        {
            private readonly DefaultRouteResolver resolver;

            public DefaultRouteResolverDiagnosticsProvider(DefaultRouteResolver resolver)
            {
                this.resolver = resolver;
            }

            public IEnumerable<object> ResolveRoute(string method, string path)
            {
                var context =
                    CreateContext(method, path);

                var results = 
                    this.resolver.Resolve(path, context, this.resolver.cache);

                return from result in results.Rejected
                       select new {
                            Reason = result.Key,
                            Routes = from route in result.Value
                                     select new
                                                {
                                                    route.Item3.Method,
                                                    route.Item3.Path,
                                                    Module = route.Item1
                                                }
                       };
            }

            private static NancyContext CreateContext(string method, string path)
            {
                return new NancyContext { Request = new Request(method, path, "http")};
            }
        }
    }
}
