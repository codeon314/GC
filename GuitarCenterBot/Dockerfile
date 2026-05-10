# Use the official .NET 8 SDK image to build the app
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the project file and restore dependencies
COPY GuitarCenterBot.csproj ./
RUN dotnet restore

# Copy the rest of the code and build the application
COPY . ./
RUN dotnet publish -c Release -o /app/publish

# Use the official ASP.NET Core runtime image to run the app
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Expose the port Railway will use
EXPOSE 8080

# Start the application
ENTRYPOINT ["dotnet", "GuitarCenterBot.dll"]