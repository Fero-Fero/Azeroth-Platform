using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

internal static class AwsIamConnectTemplate
{
    public const string ReadOnlyTier = "ReadOnly";
    public const string StandardTier = "Standard";
    public const string FullTier = "Full";

    public static IReadOnlyList<CloudAuthAwsTemplateDto> BuildAll(string platformAccountId, string externalId)
        =>
        [
            Build(platformAccountId, externalId, ReadOnlyTier),
            Build(platformAccountId, externalId, StandardTier),
            Build(platformAccountId, externalId, FullTier),
        ];

    public static CloudAuthAwsTemplateDto Build(string platformAccountId, string externalId, string policyTier)
    {
        var tier = NormalizeTier(policyTier);
        var (label, description, actions) = tier switch
        {
            ReadOnlyTier => (
                "Read only",
                "List regions and running EC2 instances.",
                ReadOnlyActions),
            StandardTier => (
                "Standard",
                "List instances and bootstrap existing EC2 via SSM.",
                StandardActions),
            _ => (
                "Full",
                "List, launch new EC2, SSM bootstrap, and security group sync.",
                FullActions),
        };

        return new CloudAuthAwsTemplateDto
        {
            PolicyTier = tier,
            Label = label,
            Description = description,
            CloudFormationYaml = RenderYaml(platformAccountId.Trim(), externalId.Trim(), tier, actions),
        };
    }

    internal static string NormalizeTier(string? policyTier)
        => string.Equals(policyTier, ReadOnlyTier, StringComparison.OrdinalIgnoreCase)
            ? ReadOnlyTier
            : string.Equals(policyTier, StandardTier, StringComparison.OrdinalIgnoreCase)
                ? StandardTier
                : FullTier;

    private static string RenderYaml(string platformAccountId, string externalId, string tier, string[] actions)
    {
        var actionLines = string.Join(
            Environment.NewLine,
            actions.Select(action => $"                - {action}"));

        return $$"""
            AWSTemplateFormatVersion: '2010-09-09'
            Description: Azeroth Platform IAM role ({{tier}}) trusted by account {{platformAccountId}}
            Parameters:
              PlatformAccountId:
                Type: String
                Default: '{{platformAccountId}}'
                AllowedPattern: '^[0-9]{12}$'
              ExternalId:
                Type: String
                Default: '{{externalId}}'
                MinLength: 8
            Resources:
              AzerothPlatformRole:
                Type: AWS::IAM::Role
                Properties:
                  RoleName: AzerothPlatformAccess
                  AssumeRolePolicyDocument:
                    Version: '2012-10-17'
                    Statement:
                      - Effect: Allow
                        Principal:
                          AWS: !Sub 'arn:aws:iam::${PlatformAccountId}:root'
                        Action: sts:AssumeRole
                        Condition:
                          StringEquals:
                            sts:ExternalId: !Ref ExternalId
                  Policies:
                    - PolicyName: AzerothPlatform{{tier}}
                      PolicyDocument:
                        Version: '2012-10-17'
                        Statement:
                          - Effect: Allow
                            Action:
            {{actionLines}}
                            Resource: '*'
            Outputs:
              RoleArn:
                Description: Paste this ARN into Azeroth Platform
                Value: !GetAtt AzerothPlatformRole.Arn
              ExternalId:
                Value: !Ref ExternalId
            """;
    }

    private static readonly string[] ReadOnlyActions =
    [
        "ec2:DescribeInstances",
        "ec2:DescribeInstanceStatus",
        "ec2:DescribeTags",
        "ec2:DescribeImages",
        "ec2:DescribeRegions",
        "ec2:DescribeInstanceTypeOfferings",
        "ec2:DescribeInstanceTypes",
    ];

    private static readonly string[] StandardActions =
    [
        ..ReadOnlyActions,
        "ssm:SendCommand",
        "ssm:GetCommandInvocation",
        "ssm:ListCommandInvocations",
        "ssm:DescribeInstanceInformation",
    ];

    private static readonly string[] FullActions =
    [
        ..StandardActions,
        "ec2:DescribeVpcs",
        "ec2:DescribeSubnets",
        "ec2:DescribeSecurityGroups",
        "ec2:RunInstances",
        "ec2:ImportKeyPair",
        "ec2:DeleteKeyPair",
        "ec2:CreateSecurityGroup",
        "ec2:AuthorizeSecurityGroupIngress",
        "ec2:CreateTags",
    ];
}
