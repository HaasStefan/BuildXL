#!/bin/bash

set -e

MY_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
source "$MY_DIR/Public/Src/Sandbox/MacOs/scripts/env.sh"

declare arg_Positional=()
# stores user-specified args that are not used by this script; added to the end of command line
declare arg_UserProvidedBxlArguments=()
declare arg_DeployDev=""
declare arg_UseDev=""
declare arg_Minimal=""
declare arg_Internal=""
# default configuration is debug
declare configuration="Debug"
declare credProviderPath=""

if [[ "${OSTYPE}" == "linux-gnu" ]]; then
    readonly HostQualifier=Linux
    readonly DeploymentFolder=linux-x64
elif [[ "${OSTYPE}" == "darwin"* ]]; then
    readonly HostQualifier=DotNetCoreMac
    readonly DeploymentFolder=osx-x64
else
    print_error "Operating system not supported: ${OSTYPE}"
    exit 1
fi

function callNuget() {
    if [[ "${OSTYPE}" == "linux-gnu" ]]; then
        $MONO_HOME/mono Shared/Tools/NuGet.exe "$@"
    elif [[ "${OSTYPE}" == "darwin"* ]]; then
        $MONO_HOME/mono Shared/Tools/NuGet.exe "$@"
    else
        print_error "Operating system not supported: ${OSTYPE}"
        return 1
    fi
}

function findMono() {
    local monoLocation=$(which mono)
    if [[ -z $monoLocation ]]; then
        print_error "Did not find Mono. Please ensure mono is installed per: https://www.mono-project.com/docs/getting-started/install/ and is accessable in your PATH"
        return 1
    else
        export MONO_HOME="$(dirname "$monoLocation")"
    fi
}

function getLkg() {
    local LKG_FILE="BuildXLLkgVersionPublic.cmd"

    if [[ -n "$arg_Internal" ]]; then
        local LKG_FILE="BuildXLLkgVersion.cmd"
    fi

    local BUILDXL_LKG_VERSION=$(grep "BUILDXL_LKG_VERSION" "$MY_DIR/Shared/Scripts/$LKG_FILE" | cut -d= -f2 | tr -d '\r')
    local BUILDXL_LKG_NAME=$(grep "BUILDXL_LKG_NAME" "$MY_DIR/Shared/Scripts/$LKG_FILE" | cut -d= -f2 | perl -pe 's/(net472|win-x64)/'${DeploymentFolder}'/g' | tr -d '\r')
    local BUILDXL_LKG_FEED_1=$(grep "BUILDXL_LKG_FEED_1" "$MY_DIR/Shared/Scripts/$LKG_FILE" | cut -d= -f2 | tr -d '\r')

    print_info "Nuget Feed: $BUILDXL_LKG_FEED_1"
    print_info "Getting package: $BUILDXL_LKG_NAME.$BUILDXL_LKG_VERSION"

    local _BUILDXL_BOOTSTRAP_OUT="$MY_DIR/Out/BootStrap"
    callNuget install -OutputDirectory "$_BUILDXL_BOOTSTRAP_OUT" -Source $BUILDXL_LKG_FEED_1 $BUILDXL_LKG_NAME -Version $BUILDXL_LKG_VERSION
    export BUILDXL_BIN="$_BUILDXL_BOOTSTRAP_OUT/$BUILDXL_LKG_NAME.$BUILDXL_LKG_VERSION"
}

function setMinimal() {
    arg_Positional+=("/q:${configuration}${HostQualifier} /f:output='$MY_DIR/Out/Bin/${outputConfiguration}/${DeploymentFolder}/*'")
}

function setInternal() {
    arg_Positional+=("/p:[Sdk.BuildXL]microsoftInternal=1")
    arg_Positional+=("/remoteTelemetry+")
    arg_Positional+=("/generateCgManifestForNugets:cg/nuget/cgmanifest.json")

    for arg in "$@"
    do
        to_lower=`printf '%s\n' "$arg" | awk '{ print tolower($0) }'`
        if [[ " $to_lower " == *"endpointsecurity"* ]]; then
            return
        fi
    done
}

function compileWithBxl() {
    local args=(
        --config "$MY_DIR/config.dsc"
        /fancyConsoleMaxStatusPips:10
        # LazySODeletion is disabled as it is flaky on linux
        # /exp:LazySODeletion
        /nowarn:11319 # DX11319: nuget version mismatch
        /logsToRetain:20
        /cachemiss
        "$@"
    )

    if [[ -z "${VSTS_BUILDXL_BIN}" ]]; then
        bash "$BUILDXL_BIN/bxl.sh" "${args[@]}"
    else
        # Currently only used on VSTS CI to allow for custom BuildXL binary execution
        bash "$VSTS_BUILDXL_BIN/bxl.sh" "${args[@]}"
    fi
}

function printHelp() {
    echo "${BASH_SOURCE[0]} [--deploy-dev] [--use-dev] [--minimal] [--internal] [--release] [--shared-comp] [--vs] [--use-adobuildrunner] [--runner-arg <arg-to-buildrunner>] [--test-method <full-test-method-name>] [--test-class <full-test-class-name>] <other-arguments>"
}

function parseArgs() {
    arg_Positional=()
    arg_UserProvidedBxlArguments=()
    while [[ $# -gt 0 ]]; do
        cmd="$1"
        case $cmd in
        --help | -h)
            printHelp
            shift
            return 1
            ;;
        --deploy-dev)
            arg_DeployDev="1"
            shift
            ;;
        --use-dev)
            arg_UseDev="1"
            shift
            ;;
        --minimal)
            arg_Minimal="1"
            shift
            ;;
        --release)
            configuration="Release"
            shift
            ;;
        --internal)
            arg_Internal="1"
            shift
            ;;
        --test-class)
            arg_Positional+=("/p:[UnitTest]Filter.testClass=$2")
            shift
            shift
            ;;
        --test-method)
            arg_Positional+=("/p:[UnitTest]Filter.testMethod=$2")
            shift
            shift
            ;;
        --shared-comp)
            arg_Positional+=("/p:[Sdk.BuildXL]useManagedSharedCompilation=1")
            shift
            ;;
        --use-adobuildrunner)
            arg_Positional+=("--use-adobuildrunner")
            shift
            ;;
        --runner-arg)
            arg_Positional+=("--runner-arg $2")
            shift
            shift
            ;;
        --vs)
            arg_Positional+=(
                "/vs"
                "/vsNew"
                "/vsTargetFramework:net6.0"
                "/vsTargetFramework:net7.0"
                "/vsTargetFramework:netstandard2.0"
                "/vsTargetFramework:netstandard2.1")
            shift
            ;;
        --disable-xunitretry)
            arg_DisableXunitRetry="1"
            shift
            ;;
        *)
            # "Script" flags (and the settings associated with them) might conflict with BuildXL arguments set by a user.
            # In such a case, user-provided bxl arguments will override any arguments set by this script.
            arg_UserProvidedBxlArguments+=("$1")
            shift
            ;;
        esac
    done
}

function deployBxl { # (fromDir, toDir)
    local fromDir="$1"
    local toDir="$2"

    mkdir -p "$toDir"
    /usr/bin/rsync -arhq "$fromDir/" "$toDir" --delete
    print_info "Successfully deployed developer build from $fromDir to: $toDir; use it with the '--use-dev' flag now."
}

function installCredProvider() {

    local dotnetLocation="$(which dotnet)"

    if [[ -z $dotnetLocation ]]; then
        print_error "Did not find dotnet. Please ensure dotnet is installed per: https://docs.microsoft.com/en-us/dotnet/core/install/linux and is accessable in your PATH"
        return 1
    fi

    local destinationFolder="$HOME/.nuget"
    local credentialProvider="$destinationFolder/plugins/netcore/CredentialProvider.Microsoft/"
    local credentialProviderExe="$credentialProvider/CredentialProvider.Microsoft.exe"

    export NUGET_CREDENTIALPROVIDERS_PATH="$credentialProvider"
    
    # If not on ADO, do not install the cred provider if it is already installed.
    # On ADO, just make sure we have the right thing, the download time is not significant for a lab build
    if [[ (! -n "$ADOBuild") && -f "$credentialProviderExe" ]];
    then
        print_info "Credential provider already installed under $destinationFolder"
        return;
    fi

    # Download the artifacts credential provider
    mkdir -p "$destinationFolder"
    wget -q -c https://github.com/microsoft/artifacts-credprovider/releases/download/v1.0.0/Microsoft.NuGet.CredentialProvider.tar.gz -O - | tar -xz -C "$destinationFolder"

    # Remove the .exe, since we want to replace it with a script that runs on Mac/Linux
    rm "$credentialProviderExe"

    # Create a new .exe with the shape of a script that calls dotnet against the dotnetcore dll
    echo "#!/bin/bash" >  "$credentialProviderExe"
    echo "exec $dotnetLocation $credentialProvider/CredentialProvider.Microsoft.dll \"\$@\"" >> "$credentialProviderExe"

    chmod u+x "$credentialProviderExe"
}

function launchCredProvider() {
    credProviderPath=$(find "$NUGET_CREDENTIALPROVIDERS_PATH" -name "CredentialProvider*.exe" -type f | head -n 1)

    if [[ -z $credProviderPath ]]; then
        print_error "Did not find a credential provider under $NUGET_CREDENTIALPROVIDERS_PATH"
        exit 1
    fi

    # CODESYNC: config.dsc. The URI needs to match the (single) feed used for the internal build
    $credProviderPath -U https://pkgs.dev.azure.com/cloudbuild/_packaging/BuildXL.Selfhost/nuget/v3/index.json -V Information -C -R
}

function setAuthenticationTokenInNpmrc() {
    # This function is responsible for setting the PAT generated for our internal selfhost feed to be used by npm
    # first parse the local npmrc to see if there already exists a valid PAT
    if ! [ -f "$HOME/.npmrc" ]; then
        # npmrc doesn't exist, lets create one one now
        touch "$HOME/.npmrc"
    else
        # delete any existing lines in the npmrc that might contain a stale token
        # existing token may be valid, but we don't need to check that here because the credential provider has already generated/cached one
        # we can just replace the existing one and save the trouble of having to verify whether it is valid by making a web request
        mv "$HOME/.npmrc" "$HOME/.npmrc.bak"
        touch "$HOME/.npmrc"

        while read line; do
            if [[ "$line" == *"//cloudbuild.pkgs.visualstudio.com/_packaging/BuildXL.Selfhost/npm/registry"* ]]; then
                continue
            fi

            echo "$line" >> "$HOME/.npmrc"
        done < "$HOME/.npmrc.bak"

        rm "$HOME/.npmrc.bak"
    fi

    # get a cached token from credential provider (it should already be cached from when we called it earlier for nuget)
    # we use the nuget uri here, but all this does is return a token with vso_packaging which is what we need for npm
    credProviderOutput=$($credProviderPath -U https://pkgs.dev.azure.com/cloudbuild/_packaging/BuildXL.Selfhost/nuget/v3/index.json -C -F Json)

    # output is in the format '{"Username":"VssSessionToken","Password":"token"}'
    token=$(echo $credProviderOutput | sed -E -e 's/.*\{"Username":"[a-zA-Z0-9]*","Password":"([a-zA-Z0-9]*)"\}.*/\1/')
    b64token=$(echo -ne "$token" | base64)

    # write new token to file
    echo "" >> "$HOME/.npmrc"
    echo "//cloudbuild.pkgs.visualstudio.com/_packaging/BuildXL.Selfhost/npm/registry/:username=VssSessionToken" >> "$HOME/.npmrc"
    echo "//cloudbuild.pkgs.visualstudio.com/_packaging/BuildXL.Selfhost/npm/registry/:_password=$b64token" >> "$HOME/.npmrc"
    echo "//cloudbuild.pkgs.visualstudio.com/_packaging/BuildXL.Selfhost/npm/registry/:email=not-used@example.com" >> "$HOME/.npmrc"
}

# allow this script to be sourced, in which case we shouldn't execute anything
if [[ "$0" != "${BASH_SOURCE[0]}" ]]; then 
    return 0
fi

# Make sure we are running in our own working directory
pushd "$MY_DIR"

parseArgs "$@"

outputConfiguration=`printf '%s' "$configuration" | awk '{ print tolower($0) }'`

if [[ -n "$arg_Internal" && -n "$TF_BUILD" ]]; then
    readonly ADOBuild="1"
fi

findMono

if [[ -n "$arg_DeployDev" || -n "$arg_Minimal" ]]; then
    setMinimal
fi

if [[ -n "$arg_Internal" ]]; then
    setInternal $@
fi

# if the nuget credential provider is not configured (and the build is an internal one, which is where it is needed)
# download and install the artifacts credential provider
if [[ -n "$arg_Internal" ]] && [[ ! -d "${NUGET_CREDENTIALPROVIDERS_PATH:-}" ]]; then
    installCredProvider
fi

# The internal build needs authentication. When not running on ADO use the configured cred provider
# to prompt for credentials as a way to guarantee the auth token will be cached for the subsequent build.
# This may prompt an interactive pop-up/console. ADO pipelines already configure the corresponding env vars 
# so there is no need to do this on that case. Once the token is cached, launching the provider shouldn't need
# any user interaction.
# For npm authentication, we write the PAT to the npmrc file under $HOME/.npmrc.
# On ADO builds, the CLOUDBUILD_BUILDXL_SELFHOST_FEED_PAT_B64 variable is set instead.
# TF_BUILD is an environment variable that is always present on ADO builds. So we use it to detect that case.
if [[ -n "$arg_Internal" &&  ! -n "$TF_BUILD" ]];then
    launchCredProvider
    setAuthenticationTokenInNpmrc
fi

# Make sure we pass the credential provider as an env var to bxl invocation
if [[ -n $NUGET_CREDENTIALPROVIDERS_PATH ]];then
    arg_Positional+=("/p:NUGET_CREDENTIALPROVIDERS_PATH=$NUGET_CREDENTIALPROVIDERS_PATH")
fi

# If this is an internal build running on ADO, the nuget authentication is non-interactive and therefore we need to setup
# VSS_NUGET_EXTERNAL_FEED_ENDPOINTS if not configured, so the Microsoft credential provider can pick that up. The script assumes the corresponding
# secrets to be exposed in the environment
if [[ -n "$arg_Internal" && -n "$ADOBuild" && (! -n $VSS_NUGET_EXTERNAL_FEED_ENDPOINTS)]];then

    if [[ (! -n $PAT1esSharedAssets) ]]; then
        print_error "Environment variable PAT1esSharedAssets is not set."
        exit 1
    fi

    if [[ (! -n $PATCloudBuild) ]]; then
        print_error "Environment variable PATCloudBuild is not set."
        exit 1
    fi

    export VSS_NUGET_EXTERNAL_FEED_ENDPOINTS="{\"endpointCredentials\":[{\"endpoint\":\"https://pkgs.dev.azure.com/1essharedassets/_packaging/BuildXL/nuget/v3/index.json\",\"password\":\"$PAT1esSharedAssets\"},{\"endpoint\":\"https://pkgs.dev.azure.com/cloudbuild/_packaging/BuildXL.Selfhost/nuget/v3/index.json\",\"password\":\"$PATCloudBuild\"}]}" 
    export CLOUDBUILD_BUILDXL_SELFHOST_FEED_PAT_B64=$(echo -ne "$PATCloudBuild" | base64)
fi

# For local builds we want to use the in-build Linux runtime (as opposed to the runtime.linux-x64.BuildXL package)
if [[ -z "$TF_BUILD" ]];then
    arg_Positional+=("/p:[Sdk.BuildXL]validateLinuxRuntime=0")
fi

# Indicates that XUnit tests should be retried due to flakiness with certain tests 
if [[ ! -n "$arg_DisableXunitRetry" ]]; then
    # RetryXunitTests will specify a retry exit code of 1 for all xunit pips, and NumXunitRetries will specify the number of times to retry the xunit pip
    arg_Positional+=("/p:RetryXunitTests=1")
    arg_Positional+=("/p:NumXunitRetries=2")
fi

if [[ -n "$arg_UseDev" ]]; then
    if [[ ! -f $MY_DIR/Out/Selfhost/Dev/bxl ]]; then
        print_error "Error: Could not find the dev deployment. Make sure you build with --deploy-dev first."
        exit 1
    fi

    export BUILDXL_BIN=$MY_DIR/Out/Selfhost/Dev
elif [[ -z "$BUILDXL_BIN" ]]; then
    getLkg
fi

# Forcing a salt here to avoid problems faced in Linux validation pipeline related to cache.
# This is related to Bug 2104538 where the cache may or may not be setting the execute bit for some executables.
if [[ $arg_UserProvidedBxlArguments != *"/p:BUILDXL_FINGERPRINT_SALT"* ]]; then
    arg_Positional+=("/p:BUILDXL_FINGERPRINT_SALT=fixForCopyFilePipBugInLinux")
fi

compileWithBxl ${arg_Positional[@]} ${arg_UserProvidedBxlArguments[@]}

if [[ -n "$arg_DeployDev" ]]; then
    deployBxl "$MY_DIR/Out/Bin/${outputConfiguration}/${DeploymentFolder}" "$MY_DIR/Out/Selfhost/Dev"
fi

popd
