using ThirdPartyDetective.Cli.Services;

namespace ThirdPartyDetective.Cli.Tests;

public class CopyrightExtractorTests
{
    [Theory]
    [InlineData("MIT License\n\nCopyright (c) 2015 Jane Doe\n\nPermission is hereby granted", "Copyright (c) 2015 Jane Doe")]
    [InlineData("Requests\nCopyright 2019 Kenneth Reitz\n", "Copyright 2019 Kenneth Reitz")]
    [InlineData("© 2020 Acme Corp. All rights reserved.", "© 2020 Acme Corp. All rights reserved.")]
    [InlineData("Copyright Joyent, Inc. and other Node contributors.", "Copyright Joyent, Inc. and other Node contributors.")]
    [InlineData(" * Copyright (c) Microsoft Corporation.", "Copyright (c) Microsoft Corporation.")]
    [InlineData("# Copyright:  2010   Jane   Doe", "Copyright: 2010 Jane Doe")]
    [InlineData("(c) 2014 Jane Doe", "(c) 2014 Jane Doe")]
    public void Finds_copyright_notices(string text, string expected)
    {
        Assert.Equal(expected, CopyrightExtractor.Extract(text));
    }

    [Theory]
    [InlineData("Copyright (c) <year> <copyright holders>")]
    [InlineData("Copyright [yyyy] [name of copyright owner]")]
    [InlineData("Copyright (C) <year>  <name of author>")]
    [InlineData("Copyright (C) 2007 Free Software Foundation, Inc. <https://fsf.org/>")]
    [InlineData("copyright notice and this permission notice shall be included in all")]
    [InlineData("COPYRIGHT HOLDERS AND CONTRIBUTORS \"AS IS\" AND ANY EXPRESS OR IMPLIED")]
    [InlineData("Copyright and related rights are waived")]
    [InlineData("(c) You must retain, in the Source form of any Derivative Works")]
    [InlineData("Copyright: This document has been placed in the public domain.")]
    [InlineData("copyrighted material")]
    [InlineData("Copyright")]
    public void Ignores_license_prose_and_template_placeholders(string text)
    {
        Assert.Null(CopyrightExtractor.Extract(text));
    }

    [Fact]
    public void Joins_a_notice_wrapped_after_a_comma()
    {
        var text = "Copyright (c) 1991 - 1995, Stichting Mathematisch Centrum Amsterdam,\nThe Netherlands.  All rights reserved.\n";

        Assert.Equal("Copyright (c) 1991 - 1995, Stichting Mathematisch Centrum Amsterdam, The Netherlands. All rights reserved.",
            CopyrightExtractor.Extract(text));
    }

    [Fact]
    public void Keeps_at_most_five_distinct_notices()
    {
        var text = string.Join('\n', Enumerable.Range(1, 8).Select(i => $"Copyright (c) 20{i:00} Author {i}")) +
                   "\nCopyright (c) 2001 Author 1";

        var notices = CopyrightExtractor.Extract(text)!.Split("; ");

        Assert.Equal(5, notices.Length);
        Assert.Equal("Copyright (c) 2001 Author 1", notices[0]);
    }
}
